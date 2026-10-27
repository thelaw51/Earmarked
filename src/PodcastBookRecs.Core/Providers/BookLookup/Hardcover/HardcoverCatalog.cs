using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Providers.BookLookup.Hardcover;

/// <summary>
/// Searches Hardcover's catalogue through its GraphQL API, with the person's own token. The query text is fixed and the
/// search term goes only in its variables. Hardcover has one entry per book but its search doesn't return an edition
/// count, so <see cref="CatalogBook.EditionCount"/> is always null. Requests are spaced at least a second apart,
/// inside Hardcover's 60 a minute. A rejected token (401) or missing scope (403) fails only the title, so an expired
/// token never stalls matching; the Status row says what's wrong. Nothing outside this folder knows Hardcover's
/// URLs or JSON.
/// </summary>
public sealed partial class HardcoverCatalog(IHttpClientFactory httpFactory, IOptions<HardcoverOptions> options, TimeProvider time)
    : IBookCatalog, IDisposable
{
    public const string HttpClientName = "hardcover";

    internal const string Query = """query Search($q: String!, $n: Int!) { search(query: $q, query_type: "Book", per_page: $n, page: 1) { results } }""";

    private const int MaxCandidates = 10;
    private const int MaxQueryLength = 200;
    private const int MaxAuthorsPerBook = 5;
    private const string BookUrlPrefix = "https://hardcover.app/books/";

    private readonly CatalogPacer _pacer = new(time, TimeSpan.FromSeconds(1));

    public string Provider => BookLookupProviders.Hardcover;

    /// <summary>The minimum gap between requests. Tests set it to zero.</summary>
    internal TimeSpan RequestGap { get => _pacer.Gap; set => _pacer.Gap = value; }

    public async Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken)
    {
        var term = string.IsNullOrWhiteSpace(author) ? title.Trim() : $"{title.Trim()} {author.Trim()}";
        var body = await _pacer.RunAsync(
            () => CatalogHttp.SendAsync(
                httpFactory.CreateClient(HttpClientName), SearchRequest(Cap(term, MaxQueryLength), MaxCandidates, options.Value.Token), "Hardcover", cancellationToken),
            cancellationToken);
        return Parse(body);
    }

    public void Dispose() => _pacer.Dispose();

    /// <summary>The search request, used by the health check too. The body is built as JSON, so the term can't change the query.</summary>
    internal static HttpRequestMessage SearchRequest(string term, int count, string token)
    {
        var body = new JsonObject { ["query"] = Query, ["variables"] = new JsonObject { ["q"] = term, ["n"] = count } };
        var request = new HttpRequestMessage(HttpMethod.Post, "v1/graphql")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Reads candidates out of a search response. A GraphQL error arrives with HTTP 200; its message can echo the
    /// search term, so the exception carries only a short error code. Entries without a numeric ID, a title or a
    /// plain slug are skipped. The link is built here from the slug, a cover is kept only from Hardcover's image host,
    /// a subtitle Hardcover also put in the title is taken off it, and text is capped to what the database stores.
    /// </summary>
    internal static IReadOnlyList<CatalogBook> Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors))
            {
                var code = errors.ValueKind == JsonValueKind.Array
                    ? errors.EnumerateArray().Select(ErrorCodeOrNull).FirstOrDefault(c => c is not null)
                    : null;
                throw new CatalogLookupFailedException(code is null ? "Hardcover returned an error." : $"Hardcover returned an error ({code}).");
            }

            var books = new List<CatalogBook>();
            foreach (var hit in root.GetProperty("data").GetProperty("search").GetProperty("results").GetProperty("hits").EnumerateArray().Take(MaxCandidates))
            {
                if (hit.ValueKind != JsonValueKind.Object || !hit.TryGetProperty("document", out var d) || d.ValueKind != JsonValueKind.Object
                    || IdOrNull(d) is not { } id
                    || String(d, "title") is not { } title
                    || String(d, "slug") is not { } slug || !SlugPattern().IsMatch(slug))
                {
                    continue;
                }

                var subtitle = String(d, "subtitle");
                if (subtitle is not null && title.Length > subtitle.Length + 2 && title.EndsWith(": " + subtitle, StringComparison.Ordinal))
                {
                    title = title[..^(subtitle.Length + 2)];
                }

                books.Add(new CatalogBook(
                    id,
                    Cap(title, Book.MaxTitleLength),
                    subtitle is null ? null : Cap(subtitle, Book.MaxTitleLength),
                    Authors(d),
                    d.TryGetProperty("release_year", out var year) && year.ValueKind == JsonValueKind.Number && year.TryGetInt32(out var y) && y is > 0 and < 10000 ? y : null,
                    null,
                    BookUrlPrefix + slug,
                    CoverOrNull(d),
                    BookLookupProviders.Hardcover));
            }

            return books;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CatalogLookupFailedException("Hardcover returned an answer that isn't the expected JSON.", ex);
        }

        static string? String(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
    }

    /// <summary>The ID as digits, whether Hardcover sends it as a string (it does today) or a number.</summary>
    private static string? IdOrNull(JsonElement d)
    {
        if (!d.TryGetProperty("id", out var v))
        {
            return null;
        }

        var id = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number when v.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        return id is not null && IdPattern().IsMatch(id) ? id : null;
    }

    private static List<string> Authors(JsonElement d) =>
        d.TryGetProperty("author_names", out var names) && names.ValueKind == JsonValueKind.Array
            ? [.. names.EnumerateArray()
                .Where(n => n.ValueKind == JsonValueKind.String)
                .Select(n => n.GetString()!.Trim())
                .Where(n => n.Length > 0)
                .Select(n => Cap(n, 100))
                .Distinct()
                .Take(MaxAuthorsPerBook)]
            : [];

    /// <summary>A cover link kept only if <see cref="CatalogMatcher.IsUsable"/> would accept it: https on Hardcover's image host.</summary>
    private static string? CoverOrNull(JsonElement d) =>
        d.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
        && image.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
        && CatalogMatcher.IsSafeLink(url.GetString(), BookLookupProviders.CoverHost(BookLookupProviders.Hardcover), allowSubdomains: true)
            ? new Uri(url.GetString()!).AbsoluteUri
            : null;

    /// <summary>A GraphQL error's machine-readable code, if it is a short plain word; never its message.</summary>
    private static string? ErrorCodeOrNull(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object
        && extensions.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
        && ErrorCodePattern().IsMatch(code.GetString()!)
            ? code.GetString()
            : null;

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex("^[0-9]{1,12}$")]
    private static partial Regex IdPattern();

    /// <summary>Lower-case words and hyphens, short enough that the built link fits <see cref="Book.MaxUrlLength"/>.</summary>
    [GeneratedRegex("^[a-z0-9-]{1,200}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[a-z-]{1,40}$")]
    private static partial Regex ErrorCodePattern();
}
