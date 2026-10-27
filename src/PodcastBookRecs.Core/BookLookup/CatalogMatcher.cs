using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Extraction;

namespace PodcastBookRecs.Core.BookLookup;

/// <summary>
/// Decides which catalogue entry, if any, is the book a mention names. Catalogue search is loose: it returns
/// omnibus editions, "summary of" knock-offs and unrelated books with similar words, and for a title that isn't a book
/// it still returns something. So a candidate must have the same title, ignoring case, punctuation, a leading article
/// and any subtitle, and, when the model named an author, an author with the same surname (one letter out is allowed,
/// for "Elliot" against "Eliot"). With no author to confirm it, a one-edition book is too likely to be a coincidence
/// ("my second book" is a real title), so it needs at least <see cref="MinEditionsWithoutAuthor"/> editions, and a
/// title made only of generic words ("the book", "his new memoir") isn't looked up at all: see <see cref="IsTooVague"/>.
/// </summary>
public static class CatalogMatcher
{
    /// <summary>Stored on every match. Bump it when these rules or <see cref="Doubts"/> change, and every mention is matched again.</summary>
    public const string Version = "match-v4";

    public const int MinEditionsWithoutAuthor = 2;

    /// <summary>
    /// A match to a book with this many editions or fewer is flagged <see cref="MatchDoubts.FewEditions"/>. A catalogue
    /// that doesn't count editions sets no flag, and its books never pass <see cref="MinEditionsWithoutAuthor"/>.
    /// </summary>
    public const int FewEditions = 2;

    private static readonly HashSet<string> NameSuffixes = new(StringComparer.Ordinal) { "jr", "sr", "ii", "iii", "iv", "phd", "md" };

    // Words that describe a book without naming one. A title made only of these is a reference, not a title.
    private static readonly HashSet<string> GenericTitleWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "my", "his", "her", "their", "your", "our", "its", "this", "that", "these", "those",
        "new", "latest", "first", "second", "third", "next", "last", "previous", "other", "own", "whole", "upcoming", "forthcoming",
        "book", "books", "novel", "novels", "memoir", "memoirs", "biography", "autobiography", "story", "stories", "essay", "essays",
        "collection", "series", "sequel", "trilogy", "volume", "manuscript", "work", "piece", "text", "title", "chapter",
    };

    // Words in a title or author name that mark a derivative work: a summary or study aid, not the book itself.
    private static readonly string[] DerivativeMarkers = ["summary", "study guide", "workbook", "sparknotes", "cliffsnotes", "analysis of"];

    /// <summary>
    /// The key two mentions share when they would get the same answer: the title key and the author's surname. Used to
    /// reuse an earlier result instead of asking the catalogue again, and to apply a <see cref="ReviewDecision"/>.
    /// Decisions are stored by this key, so changing what it returns (here, in <see cref="MentionMerger.TitleKey"/> or
    /// in <see cref="Surname"/>) needs a step that re-keys them from their stored title and author.
    /// </summary>
    public static string LookupKey(string title, string? author) => $"{MentionMerger.TitleKey(title)}|{Surname(author)}";

    /// <summary>True when the author text yields a surname to check a candidate against.</summary>
    public static bool HasSurname(string? author) => Surname(author).Length > 0;

    /// <summary>
    /// The candidate that is this book, or null if none qualifies. One matching the whole title beats one matching only
    /// the part before a subtitle, so "Dune Messiah: A Novel" finds Dune Messiah, not the more popular Dune; after
    /// that, most editions wins.
    /// </summary>
    public static CatalogBook? Pick(string title, string? author, IReadOnlyList<CatalogBook> candidates)
    {
        var wanted = TitleKeys(title, null);
        if (wanted.Count == 0 || IsTooVague(title, author))
        {
            return null;
        }

        var full = MentionMerger.TitleKey(title);
        var surname = Surname(author);
        return candidates
            .Where(c => TitleKeys(c.Title, c.Subtitle).Overlaps(wanted))
            .Where(c => !IsDerivative(c, title))
            .Where(c => surname.Length == 0 ? c.EditionCount >= MinEditionsWithoutAuthor : c.Authors.Any(a => SameSurname(Surname(a), surname)))
            .OrderByDescending(c => TitleKeys(c.Title, c.Subtitle).Contains(full))
            .ThenByDescending(c => c.EditionCount)
            .FirstOrDefault();
    }

    /// <summary>
    /// Why <paramref name="picked"/>, chosen by <see cref="Pick"/> for this title and author, might still be the wrong
    /// book. The review queue shows matches with any doubt to a person.
    /// </summary>
    public static MatchDoubts Doubts(string? author, CatalogBook picked)
    {
        var doubts = picked.EditionCount <= FewEditions ? MatchDoubts.FewEditions : MatchDoubts.None;
        var surname = Surname(author);
        if (surname.Length == 0)
        {
            return doubts | MatchDoubts.NoAuthor;
        }

        return picked.Authors.Any(a => Surname(a) == surname) ? doubts : doubts | MatchDoubts.FuzzySurname;
    }

    /// <summary>
    /// True for a title made only of generic words with no author to pin it down: the model has turned "the book" or
    /// "my new memoir" into a title. Catalogues do have books called "The Book", so with an author the title is still
    /// looked up and the surname decides.
    /// </summary>
    public static bool IsTooVague(string title, string? author) =>
        Surname(author).Length == 0
        && title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(QuoteLocator.Normalise).Where(w => w.Length > 0) is var words
        && words.Any()
        && words.All(GenericTitleWords.Contains);

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

    /// <summary>
    /// A summary or study aid rather than the book. A marker the mention's own title contains doesn't count, so a book
    /// really called "An Analysis of Algorithms" can still match.
    /// </summary>
    private static bool IsDerivative(CatalogBook candidate, string mentioned) =>
        DerivativeMarkers.Where(marker => !mentioned.Contains(marker, StringComparison.OrdinalIgnoreCase)).Any(marker =>
            candidate.Title.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || candidate.Authors.Any(a => a.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Equal, or for names of five letters or more, one letter added, dropped or changed.</summary>
    private static bool SameSurname(string a, string b) =>
        a == b || (Math.Min(a.Length, b.Length) >= 5 && Math.Abs(a.Length - b.Length) <= 1 && OneEditApart(a, b));

    private static bool OneEditApart(string a, string b)
    {
        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }

        var i = 0;
        while (i < a.Length && a[i] == b[i])
        {
            i++;
        }

        // Past the first difference, the rest must line up: after a change if the lengths match, after an insertion if not.
        return a.Length == b.Length ? a.AsSpan(i + 1).SequenceEqual(b.AsSpan(i + 1)) : a.AsSpan(i).SequenceEqual(b.AsSpan(i + 1));
    }

    /// <summary>
    /// The surname, normalised: the last word of the name, or the part before a comma for "Tolkien, J.R.R.", skipping
    /// suffixes. "J.R.R. Tolkien", "Tolkien, J. R. R." and "Tolkien" all give "tolkien"; "Martin Luther King Jr." gives "king".
    /// </summary>
    private static string Surname(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        // "Surname, Given" puts the surname first; "Given Surname, Jr." only looks like it.
        var parts = name.Split(',', 2);
        if (parts.Length == 2 && !NameSuffixes.Contains(QuoteLocator.Normalise(parts[1])))
        {
            name = parts[0];
        }

        return name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(QuoteLocator.Normalise)
            .LastOrDefault(w => w.Length > 0 && !NameSuffixes.Contains(w)) ?? "";
    }

    /// <summary>
    /// A picked book was copied from a search result or built from what a person typed, and it sat in the database in
    /// between. Before it becomes a <see cref="Book"/>, check what every entry must have: a known provider, an ID, a
    /// title, and https links, the book's own on that catalogue's host. A <see cref="BookLookupProviders.Manual"/> entry
    /// is the one kind with no link, and its ID must be a GUID (its decision's). The review page checks a pick with this when it's made, and the matcher again
    /// before adding it. The record's types say most of these aren't null, but JSON read back from the database can
    /// still hold nulls.
    /// </summary>
    public static bool IsUsable(CatalogBook? pick) =>
        pick is not null
        && !string.IsNullOrWhiteSpace(pick.CatalogId) && pick.CatalogId.Length <= MaxCatalogIdLength
        && !string.IsNullOrWhiteSpace(pick.Title)
        && pick.Authors is not null && pick.Authors.All(a => a is not null)
        && (pick.CoverUrl is null || IsHttps(pick.CoverUrl))
        && pick.ProviderOrDefault switch
        {
            BookLookupProviders.Manual => pick.InfoUrl is null && pick.CoverUrl is null && Guid.TryParseExact(pick.CatalogId, "N", out _),
            var provider => BookLookupProviders.Known.Contains(provider) && IsHttps(pick.InfoUrl)
                && new Uri(pick.InfoUrl!).Host == BookLookupProviders.InfoHost(provider),
        };

    private const int MaxCatalogIdLength = 40;

    private static bool IsHttps(string? url) =>
        url is not null && url.Length <= Book.MaxUrlLength && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
