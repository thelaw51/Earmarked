using System.Text.RegularExpressions;

namespace Earmarked.Core.Extraction;

/// <summary>Why <see cref="MentionScreen"/> dropped a mention.</summary>
public enum ScreenReason
{
    /// <summary>The model gave no words showing it is a book.</summary>
    NoEvidence,

    /// <summary>The title is the podcast's own name.</summary>
    ShowName,

    /// <summary>The title ends in a word naming another medium, such as "podcast" or "film".</summary>
    OtherMedium,

    /// <summary>The title only points at a book: "her book", "Mara Quill's new book", "the Quill books".</summary>
    Reference,

    /// <summary>The title is the author's name.</summary>
    AuthorName,

    /// <summary>The title is a newspaper, website or device that small models often report as a book.</summary>
    NotABook,
}

/// <summary>
/// Drops mentions that can't be books, after their quotes are checked and before they are merged and stored. The
/// prompt asks the model to skip all of these; the screen catches the ones it reports anyway. Rules are structural
/// where possible, with short word lists only for names the model keeps returning.
/// </summary>
public static partial class MentionScreen
{
    /// <summary>Words that end a title naming something other than a book.</summary>
    private static readonly HashSet<string> OtherMediumWords = new(StringComparer.Ordinal) { "podcast", "podcasts", "film", "films", "movie", "movies", "documentary" };

    /// <summary>Title keys of newspapers and devices the model has reported as books.</summary>
    private static readonly HashSet<string> NonBookTitles = new(StringComparer.Ordinal)
    {
        "financial times", "new york times", "wall street journal", "washington post", "telegraph", "ipad", "iphone", "kindle",
    };

    /// <summary>Websites whose name anywhere in a title means it is about the site ("the Wikipedia page for ...").</summary>
    private static readonly HashSet<string> SiteWords = new(StringComparer.Ordinal) { "wikipedia", "goodreads" };

    /// <summary>Words a reference to a book is made of. A title made only of these, with a book word, names no book.</summary>
    private static readonly HashSet<string> ReferenceWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "my", "his", "her", "their", "our", "your", "this", "that", "new", "latest", "first", "last", "next", "own", "debut",
        "great", "good", "recent", "favourite", "favorite", "old",
    };

    /// <summary>The nouns of a reference: "her new book", "a great paper".</summary>
    private static readonly HashSet<string> BookWords = new(StringComparer.Ordinal)
    {
        "book", "books", "novel", "novels", "memoir", "memoirs", "autobiography", "biography", "paper", "papers", "article", "articles",
    };

    /// <summary>
    /// Last words that make a title point at several books: "the Quill books", "the Harbour series". Not "trilogy",
    /// which is often part of a published omnibus's title.
    /// </summary>
    private static readonly HashSet<string> CollectionWords = new(StringComparer.Ordinal) { "books", "series" };

    /// <summary>The first rule <paramref name="mention"/> breaks, or null when it may be a book.</summary>
    public static ScreenReason? Check(ExtractedMention mention, string podcastTitle) =>
        LacksEvidence(mention) ? ScreenReason.NoEvidence
        : IsShowName(mention.Title, podcastTitle) ? ScreenReason.ShowName
        : NamesOtherMedium(mention.Title) ? ScreenReason.OtherMedium
        : IsReference(mention.Title, mention.Author) ? ScreenReason.Reference
        : IsAuthorName(mention.Title, mention.Author) ? ScreenReason.AuthorName
        : IsKnownNonBook(mention.Title) ? ScreenReason.NotABook
        : null;

    /// <summary>The prompt asks for the words that show it is a book; a model that gives none is guessing.</summary>
    internal static bool LacksEvidence(ExtractedMention mention) => string.IsNullOrWhiteSpace(mention.Evidence);

    /// <summary>
    /// The title's key is the podcast's. Exact only: stripping words such as "podcast" from the show's name would
    /// also drop a book the show is named after.
    /// </summary>
    internal static bool IsShowName(string title, string podcastTitle)
    {
        var key = MentionMerger.TitleKey(title);
        return key.Length > 0 && key == MentionMerger.TitleKey(podcastTitle);
    }

    /// <summary>
    /// The last word is "podcast", "film" or the like ("The Long Walk Podcast"), or a bracketed note says so
    /// ("Harbour Lights (film)", "Harbour Lights (TV series)").
    /// </summary>
    internal static bool NamesOtherMedium(string title) =>
        (Words(title) is [.., var last] && OtherMediumWords.Contains(last)) || MediumNote().IsMatch(title);

    /// <summary>
    /// The title only refers to a book: made only of words like "her new book" or "memoir", a possessive such as
    /// "Mara Quill's book" or "Mara Quill's books on sailing", a collection such as "the Quill books" or "the Harbour
    /// series", or "the X Y book" without an author (two or more words before "book", so "The Jungle Book" stays).
    /// </summary>
    internal static bool IsReference(string title, string? author)
    {
        var words = Words(title);
        if (words.Count == 0)
        {
            return false;
        }

        if (words.All(w => ReferenceWords.Contains(w) || BookWords.Contains(w)) && words.Any(BookWords.Contains))
        {
            return true;
        }

        if (PossessiveReference().IsMatch(title.Trim()) || (words.Count > 1 && CollectionWords.Contains(words[^1])))
        {
            return true;
        }

        return author is null && words.Count >= 4 && words[0] == "the" && words[^1] == "book";
    }

    /// <summary>
    /// The title is the author's name, or the author's surname alone. Spaces are ignored, so "C.S. Quill" and
    /// "C. S. Quill" are the same name.
    /// </summary>
    internal static bool IsAuthorName(string title, string? author)
    {
        if (author is null)
        {
            return false;
        }

        var key = MentionMerger.TitleKey(title).Replace(" ", "", StringComparison.Ordinal);
        var authorWords = Words(author);
        return key.Length > 0 && authorWords.Count > 0 && (key == string.Concat(authorWords) || key == authorWords[^1]);
    }

    /// <summary>
    /// A newspaper or device from <see cref="NonBookTitles"/>, any title naming a site from <see cref="SiteWords"/>,
    /// or a title that is a web address ("harbourlights.com").
    /// </summary>
    internal static bool IsKnownNonBook(string title) =>
        NonBookTitles.Contains(MentionMerger.TitleKey(title)) || Words(title).Any(SiteWords.Contains) || WebAddress().IsMatch(title);

    /// <summary>Lower-case words, letters and digits only, as <see cref="QuoteLocator"/> compares them.</summary>
    private static List<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(QuoteLocator.Normalise).Where(w => w.Length > 0).ToList();

    /// <summary>"Name's [new] book", optionally followed by "on/about/by/from ...".</summary>
    [GeneratedRegex(@"^\S.*['’]s\s+((new|latest|first|last|next|own|debut)\s+)*(book|books|novel|novels|memoir|memoirs|autobiography|biography)(\s+(on|about|by|from)\s.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PossessiveReference();

    [GeneratedRegex(@"\((?:[^)]*\s)?(film|movie|tv|series|documentary|podcast)\b[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex MediumNote();

    [GeneratedRegex(@"(^|\s)(https?://|www\.)|\b[a-z0-9-]+\.(com|org|net|tv|io|co\.uk)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WebAddress();
}
