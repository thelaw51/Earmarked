using PodcastBookRecs.Core.Extraction;

namespace PodcastBookRecs.Core.BookLookup;

/// <summary>
/// Decides which catalogue entry, if any, is the book a mention names. Catalogue search is loose: it returns
/// omnibus editions, "summary of" knock-offs and unrelated books with similar words, and for a title that isn't a book
/// it still returns something. So a candidate must have the same title, ignoring case, punctuation, a leading article
/// and any subtitle, and, when the model named an author, an author with the same surname.
/// </summary>
public static class CatalogMatcher
{
    /// <summary>Stored on every match. Bump it when these rules change, and every mention is matched again.</summary>
    public const string Version = "match-v1";

    // Words in a title or author name that mark a derivative work: a summary or study aid, not the book itself.
    private static readonly string[] DerivativeMarkers = ["summary", "study guide", "workbook", "sparknotes", "cliffsnotes", "analysis of"];

    /// <summary>
    /// The key two mentions share when they would get the same answer: the title key and the author's surname. Used to
    /// reuse an earlier result instead of asking the catalogue again.
    /// </summary>
    public static string LookupKey(string title, string? author) => $"{MentionMerger.TitleKey(title)}|{Surname(author)}";

    /// <summary>The candidate that is this book, preferring the one with most editions, or null if none qualifies.</summary>
    public static CatalogBook? Pick(string title, string? author, IReadOnlyList<CatalogBook> candidates)
    {
        var wanted = TitleKeys(title, null);
        if (wanted.Count == 0)
        {
            return null;
        }

        var surname = Surname(author);
        return candidates
            .Where(c => TitleKeys(c.Title, c.Subtitle).Overlaps(wanted))
            .Where(c => !IsDerivative(c))
            .Where(c => surname.Length == 0 || c.Authors.Any(a => Surname(a) == surname))
            .MaxBy(c => c.EditionCount);
    }

    /// <summary>The full title, the title before any subtitle, and (for a candidate) title plus subtitle, all as keys.</summary>
    private static HashSet<string> TitleKeys(string title, string? subtitle)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { MentionMerger.TitleKey(title) };
        if (title.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon)
        {
            keys.Add(MentionMerger.TitleKey(title[..colon]));
        }

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            keys.Add(MentionMerger.TitleKey($"{title} {subtitle}"));
        }

        keys.Remove("");
        return keys;
    }

    private static bool IsDerivative(CatalogBook candidate) =>
        DerivativeMarkers.Any(marker =>
            candidate.Title.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || candidate.Authors.Any(a => a.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The last word of a name, normalised: "J.R.R. Tolkien" and "Tolkien" both give "tolkien".</summary>
    private static string Surname(string? name) =>
        name?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(QuoteLocator.Normalise).LastOrDefault(w => w.Length > 0) ?? "";
}
