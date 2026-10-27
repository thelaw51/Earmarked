using System.Globalization;
using System.Text;
using System.Text.Json;
using PodcastBookRecs.Core.Books;

namespace PodcastBookRecs.Web.Services;

/// <summary>
/// Writes the filtered book list as a file: a plain CSV, a CSV that Goodreads imports, or JSON with every mention.
/// Titles, authors and quotes come from the catalogue or the model, so every CSV cell is escaped and any that a
/// spreadsheet would run as a formula is made plain text.
/// </summary>
public static class BookExport
{
    /// <summary>
    /// The columns Goodreads' own export uses, by the same names, which its importer and others (BookWyrm, StoryGraph)
    /// read. Only the ones this app knows are written; Goodreads matches on title and author when there's no ISBN.
    /// </summary>
    public static readonly string[] GoodreadsColumns =
        ["Title", "Author", "Additional Authors", "ISBN", "ISBN13", "My Rating", "Original Publication Year", "Date Added", "Bookshelves", "Exclusive Shelf", "Private Notes"];

    private const string GoodreadsShelf = "to-read";

    private static readonly string[] CsvColumns =
        ["Rank", "Title", "Authors", "First published", "Podcasts", "Episodes", "Recommended in", "Last mentioned", "Mentioned on", "Catalogue link"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>One row per book, in list order. Starts with a byte order mark so Excel reads it as UTF-8.</summary>
    public static byte[] Csv(IReadOnlyList<BookListRow> rows, IReadOnlyList<ListedMention> mentions, TimeZoneInfo zone)
    {
        var podcasts = PodcastsByBook(mentions);
        var csv = new StringBuilder();
        AppendRow(csv, CsvColumns);
        foreach (var row in rows)
        {
            AppendRow(csv,
            [
                Number(row.Rank),
                row.Book.Title,
                row.Book.Authors,
                row.Book.FirstPublishYear is { } year ? Number(year) : "",
                Number(row.Ranking.Podcasts),
                Number(row.Ranking.Episodes),
                Number(row.Ranking.RecommendedIn),
                LocalDate(row.Ranking.LastMentioned, zone, "yyyy-MM-dd"),
                string.Join("; ", podcasts.GetValueOrDefault(row.Book.Id, [])),
                Display.HttpsOrNull(row.Book.InfoUrl) ?? "",
            ]);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    /// <summary>
    /// Goodreads' import format, every book on the to-read shelf, with the podcasts that mentioned it in the private
    /// notes. No byte order mark: it would become part of the first column's name.
    /// </summary>
    public static byte[] GoodreadsCsv(IReadOnlyList<BookListRow> rows, IReadOnlyList<ListedMention> mentions, TimeZoneInfo zone, DateTime nowUtc)
    {
        var podcasts = PodcastsByBook(mentions);
        var added = LocalDate(nowUtc, zone, "yyyy/MM/dd");
        var csv = new StringBuilder();
        AppendRow(csv, GoodreadsColumns);
        foreach (var row in rows)
        {
            var authors = row.Book.Authors.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppendRow(csv,
            [
                row.Book.Title,
                authors.FirstOrDefault() ?? "",
                string.Join(", ", authors.Skip(1)),
                "",
                "",
                "0",
                row.Book.FirstPublishYear is { } year ? Number(year) : "",
                added,
                GoodreadsShelf,
                GoodreadsShelf,
                $"Mentioned on {string.Join(", ", podcasts.GetValueOrDefault(row.Book.Id, []))}.",
            ]);
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    /// <summary>Every book with the mentions that counted for it, and the filters the list was made with.</summary>
    public static byte[] Json(IReadOnlyList<BookListRow> rows, IReadOnlyList<ListedMention> mentions, BookListParameters parameters, DateTime nowUtc)
    {
        var byBook = mentions.ToLookup(m => m.BookId);
        var export = new JsonExport(
            nowUtc,
            new JsonFilter(parameters.Podcasts, parameters.RecommendedOnly, parameters.From, parameters.To, parameters.Text),
            rows.Select(r => new JsonBook(
                r.Rank,
                r.Book.Title,
                r.Book.Authors,
                r.Book.FirstPublishYear,
                Display.HttpsOrNull(r.Book.InfoUrl),
                Display.HttpsOrNull(r.Book.CoverUrl),
                r.Ranking.Podcasts,
                r.Ranking.Episodes,
                r.Ranking.RecommendedIn,
                r.Ranking.LastMentioned,
                byBook[r.Book.Id]
                    .Select(m => new JsonMention(m.Podcast, m.Episode, m.PublishedAt, m.Kind.ToString(), m.StartTime?.TotalSeconds, m.Quote))
                    .ToList()))
                .ToList());
        return JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);
    }

    /// <summary>
    /// One CSV cell: always quoted, quotes doubled, and a leading <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or
    /// carriage return prefixed with <c>'</c> so a spreadsheet shows it as text instead of running it.
    /// </summary>
    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static void AppendRow(StringBuilder csv, IEnumerable<string> cells) =>
        csv.Append(string.Join(',', cells.Select(Cell))).Append("\r\n");

    private static Dictionary<Guid, List<string>> PodcastsByBook(IReadOnlyList<ListedMention> mentions) =>
        mentions
            .GroupBy(m => m.BookId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Podcast).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList());

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string LocalDate(DateTime? utc, TimeZoneInfo zone, string format) =>
        utc is { } value ? TimeZoneInfo.ConvertTimeFromUtc(value, zone).ToString(format, CultureInfo.InvariantCulture) : "";

    private sealed record JsonExport(DateTime ExportedAt, JsonFilter Filter, IReadOnlyList<JsonBook> Books);

    private sealed record JsonFilter(IReadOnlyList<Guid> Podcasts, bool RecommendedOnly, DateOnly? From, DateOnly? To, string? Text);

    private sealed record JsonBook(
        int Rank, string Title, string Authors, int? FirstPublished, string? CatalogueUrl, string? CoverUrl, int Podcasts, int Episodes,
        int RecommendedIn, DateTime? LastMentioned, IReadOnlyList<JsonMention> Mentions);

    private sealed record JsonMention(string Podcast, string Episode, DateTime? PublishedAt, string Kind, double? AtSeconds, string Quote);
}
