using PodcastBookRecs.Core.BookLookup;

namespace PodcastBookRecs.Core.Data;

/// <summary>
/// A person's ruling on a title and author, applied to every mention with the same <see cref="LookupKey"/>: on every
/// podcast, and to mentions extracted later. Kept apart from <see cref="BookMention"/> because mentions are deleted and
/// recreated by re-extraction, and re-matched whenever <c>CatalogMatcher.Version</c> changes. The matcher reads this
/// first, so a decision outlives both. Only the matcher applies it to mentions.
/// </summary>
public class ReviewDecision
{
    public const int MaxNoteLength = 500;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// <c>CatalogMatcher.LookupKey</c> of <see cref="Title"/> and <see cref="Author"/>. If the key rules ever change,
    /// decisions must be re-keyed from those two columns, or they silently stop applying.
    /// </summary>
    public required string LookupKey { get; set; }

    /// <summary>The title and author as the mention had them when the decision was made.</summary>
    public required string Title { get; set; }
    public string? Author { get; set; }

    public ReviewVerdict Verdict { get; set; }

    /// <summary>
    /// For <see cref="ReviewVerdict.Corrected"/>: the catalogue entry the person picked, copied from a stored search
    /// result. For <see cref="ReviewVerdict.KeptAsIs"/>: the entry built from what the person typed. The matcher adds
    /// it as a <see cref="Book"/> and sets <see cref="BookId"/>.
    /// </summary>
    public CatalogBook? Pick { get; set; }

    /// <summary>
    /// The book mentions with this key point at, for <see cref="ReviewVerdict.Confirmed"/> and, once added,
    /// <see cref="ReviewVerdict.Corrected"/> and <see cref="ReviewVerdict.KeptAsIs"/>.
    /// </summary>
    public Guid? BookId { get; set; }
    public Book? Book { get; set; }

    /// <summary>Why, in the person's words. Plain text, never rendered as markup.</summary>
    public string? Note { get; set; }
    public DateTime DecidedAt { get; set; }
}

/// <summary>What a person decided about a mention. Values are stored, so never renumber them.</summary>
public enum ReviewVerdict
{
    /// <summary>The catalogue's match is right.</summary>
    Confirmed = 0,

    /// <summary>The right book is <see cref="ReviewDecision.Pick"/>, found by searching the catalogue.</summary>
    Corrected = 1,

    /// <summary>Not a book at all, e.g. a newspaper or an unnamed "my last book".</summary>
    NotABook = 2,

    /// <summary>A book, but not the one the catalogue found, and no right one was picked.</summary>
    WrongBook = 3,

    /// <summary>
    /// A real book that no catalogue has. <see cref="ReviewDecision.Pick"/> holds the title and author the person
    /// typed, as a <c>Manual</c> entry with no link, and the matcher adds it as a <see cref="Book"/>.
    /// </summary>
    KeptAsIs = 4,
}
