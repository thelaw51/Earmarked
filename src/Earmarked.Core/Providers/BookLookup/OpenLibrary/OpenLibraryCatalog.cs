using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;

namespace Earmarked.Core.Providers.BookLookup.OpenLibrary;

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
    private const int MaxQueryLength = 200;
    private const int MaxAuthorsPerBook = 5;
    private const string Fields = "key,title,subtitle,author_name,first_publish_year,cover_i,edition_count";

    private readonly CatalogPacer _pacer = new(time, TimeSpan.FromSeconds(1));

    public string Provider => BookLookupProviders.OpenLibrary;

    /// <summary>The minimum gap between requests. Tests set it to zero.</summary>
    internal TimeSpan RequestGap { get => _pacer.Gap; set => _pacer.Gap = value; }

    public async Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken)
    {
        var query = $"search.json?title={Escape(title)}" + (string.IsNullOrWhiteSpace(author) ? "" : $"&author={Escape(author)}")
            + $"&fields={Fields}&limit={MaxCandidates}";

        return Parse(await _pacer.RunAsync(() => SendAsync(query, cancellationToken), cancellationToken));
    }

    public void Dispose() => _pacer.Dispose();

    private Task<string> SendAsync(string query, CancellationToken cancellationToken) =>
        CatalogHttp.SendAsync(httpFactory.CreateClient(HttpClientName), new HttpRequestMessage(HttpMethod.Get, query), "Open Library", cancellationToken);

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
                        .Select(n => CatalogText.Cap(n.GetString()!.Trim(), 100)).Distinct().Take(MaxAuthorsPerBook).ToList()
                    : [];
                var cover = Number(d, "cover_i") is > 0 and var c ? $"https://covers.openlibrary.org/b/id/{c.ToString(CultureInfo.InvariantCulture)}-M.jpg" : null;
                books.Add(new CatalogBook(
                    id,
                    CatalogText.Cap(title, Book.MaxTitleLength),
                    String(d, "subtitle") is { } subtitle ? CatalogText.Cap(subtitle, Book.MaxTitleLength) : null,
                    authors,
                    Number(d, "first_publish_year") is > 0 and < 10000 and var year ? (int)year : null,
                    Number(d, "edition_count") is > 0 and <= int.MaxValue and var editions ? (int)editions : 0,
                    $"https://openlibrary.org/works/{id}",
                    cover,
                    BookLookupProviders.OpenLibrary));
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

    private static string Escape(string value) => Uri.EscapeDataString(CatalogText.Cap(value.Trim(), MaxQueryLength));

    [GeneratedRegex(@"^/works/(OL[0-9]{1,12}W)$")]
    private static partial Regex WorkKey();
}
