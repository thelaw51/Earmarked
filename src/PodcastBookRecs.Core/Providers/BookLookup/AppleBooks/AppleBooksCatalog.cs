using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Providers.BookLookup.AppleBooks;

/// <summary>
/// Searches the Apple Books store through Apple's keyless iTunes Search API (<c>/search</c> with <c>media=ebook</c>).
/// The API has one free-text <c>term</c>, so the title and author go in together and <see cref="CatalogMatcher"/>
/// checks both. Apple asks for about 20 requests a minute, so requests are spaced at least 3 seconds apart. The store
/// has one entry per edition and no edition count, so <see cref="CatalogBook.EditionCount"/> is always null. Apple
/// answers HTTP 403 when a client goes over its limit, so here a 403 pauses like a 429 instead of failing the title.
/// Nothing outside this folder knows Apple's URLs or JSON.
/// </summary>
public sealed partial class AppleBooksCatalog(IHttpClientFactory httpFactory, IOptions<AppleBooksOptions> options, TimeProvider time)
    : IBookCatalog, IDisposable
{
    public const string HttpClientName = "applebooks";

    /// <summary>Statuses Apple uses to say "slow down", on top of 429 and 5xx.</summary>
    public static readonly IReadOnlyCollection<HttpStatusCode> SlowDownStatuses = [HttpStatusCode.Forbidden];

    private const int MaxCandidates = 10;
    private const int MaxQueryLength = 200;
    private const int MaxAuthorsPerBook = 5;

    private readonly CatalogPacer _pacer = new(time, TimeSpan.FromSeconds(3));

    public string Provider => BookLookupProviders.AppleBooks;

    /// <summary>The minimum gap between requests. Tests set it to zero.</summary>
    internal TimeSpan RequestGap { get => _pacer.Gap; set => _pacer.Gap = value; }

    public async Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken)
    {
        var country = options.Value.Country;
        var term = string.IsNullOrWhiteSpace(author) ? title.Trim() : $"{title.Trim()} {author.Trim()}";
        var query = $"search?term={Uri.EscapeDataString(CatalogText.Cap(term, MaxQueryLength))}&media=ebook&entity=ebook&country={country}&limit={MaxCandidates}";

        var body = await _pacer.RunAsync(
            () => CatalogHttp.SendAsync(
                httpFactory.CreateClient(HttpClientName), new HttpRequestMessage(HttpMethod.Get, query), "Apple Books", cancellationToken, SlowDownStatuses),
            cancellationToken);
        return Parse(body, country);
    }

    public void Dispose() => _pacer.Dispose();

    /// <summary>
    /// Reads candidates out of a search response. Entries without a numeric <c>trackId</c> or a title are skipped. The
    /// link is built here from the ID and the country, and a cover is kept only from Apple's image host; text is capped
    /// to what the database stores.
    /// </summary>
    internal static IReadOnlyList<CatalogBook> Parse(string body, string country)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var books = new List<CatalogBook>();
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray().Take(MaxCandidates))
            {
                if (r.ValueKind != JsonValueKind.Object
                    || !r.TryGetProperty("trackId", out var idValue) || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt64(out var id) || id <= 0
                    || String(r, "trackName") is not { } title)
                {
                    continue;
                }

                var trackId = id.ToString(CultureInfo.InvariantCulture);
                var authors = String(r, "artistName") is { } artist ? SplitAuthors(artist) : [];
                books.Add(new CatalogBook(
                    trackId,
                    CatalogText.Cap(title, Book.MaxTitleLength),
                    null,
                    authors,
                    String(r, "releaseDate") is { } released
                        && DateTimeOffset.TryParse(released, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.Year : null,
                    null,
                    $"https://books.apple.com/{country}/book/id{trackId}",
                    CoverOrNull(String(r, "artworkUrl100")),
                    BookLookupProviders.AppleBooks));
            }

            return books;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CatalogLookupFailedException("Apple Books returned an answer that isn't the expected JSON.", ex);
        }

        static string? String(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;
    }

    /// <summary>A cover link kept only if <see cref="CatalogMatcher.IsUsable"/> would accept it: https on Apple's image host.</summary>
    private static string? CoverOrNull(string? url) =>
        CatalogMatcher.SafeLinkOrNull(url, BookLookupProviders.CoverHost(BookLookupProviders.AppleBooks), allowSubdomains: true);

    /// <summary>
    /// Apple lists co-authors in one string: "A & B", "A, B & C" or "A and B". A comma before a suffix ("Martin Luther
    /// King, Jr.") isn't a separator, so a piece that is only a suffix goes back onto the name before it; split off, it
    /// would be a second "author" with no surname.
    /// </summary>
    internal static List<string> SplitAuthors(string artist)
    {
        var names = new List<string>();
        foreach (var piece in AuthorSeparator().Split(artist).Select(a => a.Trim()).Where(a => a.Length > 0))
        {
            if (names.Count > 0 && NameSuffixes.Contains(piece.TrimEnd('.')))
            {
                names[^1] = $"{names[^1]}, {piece}";
            }
            else
            {
                names.Add(piece);
            }
        }

        return [.. names.Select(a => CatalogText.Cap(a, 100)).Distinct().Take(MaxAuthorsPerBook)];
    }

    private static readonly HashSet<string> NameSuffixes = new(StringComparer.OrdinalIgnoreCase) { "Jr", "Sr", "II", "III", "IV", "PhD", "MD" };

    [GeneratedRegex(@"\s*(?:&|,|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorSeparator();
}
