using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Text;

namespace Earmarked.Core.Providers.BookLookup.OpenLibrary;

/// <summary>
/// Searches Open Library's <c>/search.json</c> by title and author. It uses the <c>title</c> and <c>author</c> fields,
/// never the free-text <c>q</c>, which happily returns an unrelated book for a phrase that isn't a title.
/// Details come from the work's own record (<c>/works/OL…W.json</c>), which has a description and subjects but no
/// ratings or page count. Requests are spaced at least a second apart, which is Open Library's limit for anonymous
/// clients and a third of the limit for identified ones. Nothing outside this folder knows Open Library's URLs or JSON.
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

    public async Task<BookDetails?> GetDetailsAsync(string catalogId, CancellationToken cancellationToken)
    {
        if (!WorkId().IsMatch(catalogId))
        {
            throw new CatalogLookupFailedException("Not an Open Library work ID.");
        }

        // A deleted or merged work answers 404 (a merge redirects, and redirects aren't followed, so it fails instead).
        var body = await _pacer.RunAsync(
            () => CatalogHttp.SendOrNullIfNotFoundAsync(
                httpFactory.CreateClient(HttpClientName), new HttpRequestMessage(HttpMethod.Get, $"works/{catalogId}.json"), "Open Library", cancellationToken),
            cancellationToken);
        return body is null ? null : ParseWork(body);
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

    /// <summary>
    /// Reads a work record. The description is Markdown, either a string or a <c>{"type": "/type/text", "value": …}</c>
    /// object; links keep only their text, and anything after a <c>---</c> rule (usually a list of editions or sources)
    /// is dropped. Subjects that are library bookkeeping rather than what the book is about are skipped.
    /// </summary>
    internal static BookDetails ParseWork(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CatalogLookupFailedException("Open Library returned an answer that isn't the expected JSON.");
            }

            var description = root.TryGetProperty("description", out var d)
                ? d.ValueKind switch
                {
                    JsonValueKind.String => d.GetString(),
                    JsonValueKind.Object when d.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
                    _ => null,
                }
                : null;

            var subjects = root.TryGetProperty("subjects", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim())
                    .Where(x => x.Length > 0 && !IsBookkeeping(x))
                    .Select(x => CatalogText.Cap(x, BookDetails.MaxSubjectLength))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(BookDetails.MaxSubjects)
                    .ToList()
                : [];

            return new BookDetails(description is null ? null : PlainText.FromHtml(FromMarkdown(description), Book.MaxDescriptionLength), subjects, null, null, null);
        }
        catch (JsonException ex)
        {
            throw new CatalogLookupFailedException("Open Library returned an answer that isn't the expected JSON.", ex);
        }
    }

    /// <summary>Markdown as Open Library writes it, reduced to its text: everything before a rule, links as their text, no emphasis marks.</summary>
    private static string FromMarkdown(string markdown)
    {
        var text = Rule().Split(markdown.ReplaceLineEndings("\n"))[0];
        text = LinkDefinition().Replace(text, "");
        text = InlineLink().Replace(text, "$1");
        text = text.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal);
        text = Escaped().Replace(text, "$1");
        return text.Replace("\n", "<br>", StringComparison.Ordinal);
    }

    /// <summary>Subjects Open Library uses for its own lists and lending ("Accessible book", "nyt:…=2008-01-01").</summary>
    private static bool IsBookkeeping(string subject) =>
        subject.Contains(':', StringComparison.Ordinal) || subject.Contains('=', StringComparison.Ordinal) || BookkeepingSubjects.Contains(subject);

    private static readonly HashSet<string> BookkeepingSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accessible book", "Protected DAISY", "In library", "Lending library", "Large type books", "Open Library Staff Picks",
        "Reading Level-Grade 11", "Reading Level-Grade 12", "Long Now Manual for Civilization", "OverDrive",
    };

    private static string Escape(string value) => Uri.EscapeDataString(CatalogText.Cap(value.Trim(), MaxQueryLength));

    [GeneratedRegex(@"^/works/(OL[0-9]{1,12}W)$")]
    private static partial Regex WorkKey();

    [GeneratedRegex(@"^OL[0-9]{1,12}W$")]
    private static partial Regex WorkId();

    [GeneratedRegex(@"\n\s*-{3,}\s*(\n|$)")]
    private static partial Regex Rule();

    [GeneratedRegex(@"(^|\n)\s*\[[^\]]{1,40}\]:\s*\S+")]
    private static partial Regex LinkDefinition();

    /// <summary><c>[text](url)</c> and <c>[text][1]</c>; the text may itself contain escaped brackets.</summary>
    [GeneratedRegex(@"\[((?:\\.|[^\]\\]){1,300})\](?:\([^)\s]{0,500}\)|\[[^\]]{0,40}\])")]
    private static partial Regex InlineLink();

    [GeneratedRegex(@"\\([\[\]()*_#\\-])")]
    private static partial Regex Escaped();
}
