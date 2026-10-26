using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Providers.BookLookup.OpenLibrary;

/// <summary>
/// Searches Open Library's <c>/search.json</c> by title and author. It uses the <c>title</c> and <c>author</c> fields,
/// never the free-text <c>q</c>, which happily returns an unrelated book for a phrase that isn't a title.
/// Requests are spaced at least a second apart, which is Open Library's limit for anonymous clients and a third of
/// the limit for identified ones. Nothing outside this folder knows Open Library's URLs or JSON.
/// </summary>
public sealed partial class OpenLibraryCatalog(IHttpClientFactory httpFactory, TimeProvider time) : IBookCatalog, IDisposable
{
    public const string HttpClientName = "openlibrary";

    private const int MaxCandidates = 10;
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxQueryLength = 200;
    private const int MaxAuthorsPerBook = 5;
    private const string Fields = "key,title,subtitle,author_name,first_publish_year,cover_i,edition_count";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    public string Provider => BookLookupProviders.OpenLibrary;

    /// <summary>The minimum gap between requests. Tests set it to zero.</summary>
    internal TimeSpan RequestGap { get; set; } = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken)
    {
        var query = $"search.json?title={Escape(title)}" + (string.IsNullOrWhiteSpace(author) ? "" : $"&author={Escape(author)}")
            + $"&fields={Fields}&limit={MaxCandidates}";

        string body;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _nextRequestAt - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, cancellationToken);
            }

            try
            {
                body = await SendAsync(query, cancellationToken);
            }
            finally
            {
                _nextRequestAt = time.GetUtcNow() + RequestGap;
            }
        }
        finally
        {
            _gate.Release();
        }

        return Parse(body);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<string> SendAsync(string query, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).GetAsync(query, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BackendUnavailableException($"Can't reach Open Library: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendUnavailableException("Open Library didn't answer in time.", ex);
        }

        using (response)
        {
            // 429 means slow down, and 5xx means Open Library is struggling: either way, pause rather than give up on titles.
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                throw new BackendUnavailableException($"Open Library returned HTTP {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CatalogLookupFailedException($"Open Library returned HTTP {(int)response.StatusCode}.");
            }

            return await ReadCappedAsync(response.Content, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the body, refusing one over <see cref="MaxResponseBytes"/>. Too big is about this answer, so it fails the
    /// title; a connection that drops while reading is about Open Library, so it pauses.
    /// </summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new CatalogLookupFailedException($"Open Library's answer is over {MaxResponseBytes / 1024} KB.");
        }

        using var buffer = new MemoryStream();
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new CatalogLookupFailedException($"Open Library's answer is over {MaxResponseBytes / 1024} KB.");
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new BackendUnavailableException($"Open Library dropped the connection: {ex.Message}", ex);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// Reads candidates out of a search response. Entries without a work key in the expected form or without a title are
    /// skipped; text is capped to what the database stores.
    /// </summary>
    internal static IReadOnlyList<CatalogBook> Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var books = new List<CatalogBook>();
            foreach (var d in doc.RootElement.GetProperty("docs").EnumerateArray().Take(MaxCandidates))
            {
                if (String(d, "key") is not { } key || WorkKey().Match(key) is not { Success: true } work || String(d, "title") is not { } title)
                {
                    continue;
                }

                var id = work.Groups[1].Value;
                var authors = d.TryGetProperty("author_name", out var names) && names.ValueKind == JsonValueKind.Array
                    ? names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                        .Select(n => Cap(n.GetString()!.Trim(), 100)).Distinct().Take(MaxAuthorsPerBook).ToList()
                    : [];
                var cover = Number(d, "cover_i") is > 0 and var c ? $"https://covers.openlibrary.org/b/id/{c.ToString(CultureInfo.InvariantCulture)}-M.jpg" : null;
                books.Add(new CatalogBook(
                    id,
                    Cap(title, Book.MaxTitleLength),
                    String(d, "subtitle") is { } subtitle ? Cap(subtitle, Book.MaxTitleLength) : null,
                    authors,
                    Number(d, "first_publish_year") is > 0 and < 10000 and var year ? (int)year : null,
                    Number(d, "edition_count") is > 0 and <= int.MaxValue and var editions ? (int)editions : 0,
                    $"https://openlibrary.org/works/{id}",
                    cover));
            }

            return books;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CatalogLookupFailedException("Open Library returned an answer that isn't the expected JSON.", ex);
        }

        static string? String(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

        static long? Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    }

    private static string Escape(string value) => Uri.EscapeDataString(Cap(value.Trim(), MaxQueryLength));

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"^/works/(OL[0-9]{1,12}W)$")]
    private static partial Regex WorkKey();
}

/// <summary>
/// Reports whether Open Library answers. Checks at most every 5 minutes and repeats the last answer in between, so the
/// Status page's 30-second schedule doesn't become a steady stream of requests. Sends no mention data.
/// </summary>
public sealed class OpenLibraryHealthCheck(IHttpClientFactory httpFactory, TimeProvider time) : IHealthCheck
{
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(5);

    private (DateTimeOffset At, HealthCheckResult Result)? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_last is { } last && time.GetUtcNow() - last.At < RecheckAfter)
        {
            return last.Result;
        }

        var problem = await BackendProbe.ProbeAsync(httpFactory.CreateClient(OpenLibraryCatalog.HttpClientName), cancellationToken);
        var result = problem is null ? HealthCheckResult.Healthy(BookLookupProviders.OpenLibrary) : HealthCheckResult.Unhealthy($"Unreachable: {problem}");
        _last = (time.GetUtcNow(), result);
        return result;
    }
}
