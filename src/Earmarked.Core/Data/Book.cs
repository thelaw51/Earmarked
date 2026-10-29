using Earmarked.Core.BookLookup;

namespace Earmarked.Core.Data;

/// <summary>
/// A published book, as a catalogue knows it. Mentions from any number of episodes and podcasts point at one book, which
/// is what lets books be ranked. Rows are only ever added: a book outlives the mentions that found it.
/// </summary>
public class Book
{
    public const int MaxTitleLength = 300;
    public const int MaxAuthorsLength = 500;
    public const int MaxUrlLength = 300;
    public const int MaxDescriptionLength = 4000;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// The catalogue it came from, e.g. <c>OpenLibrary</c>, or <c>Manual</c> for a book kept as is on the review page.
    /// With <see cref="CatalogId"/>, identifies the book.
    /// </summary>
    public required string Provider { get; set; }

    /// <summary>
    /// The catalogue's own identifier for the work (all editions together), e.g. <c>OL27513W</c>. A <c>Manual</c> book
    /// uses the ID of the decision that kept it.
    /// </summary>
    public required string CatalogId { get; set; }
    public required string Title { get; set; }

    /// <summary>Authors as the catalogue lists them, joined with ", ". Empty when the catalogue has none.</summary>
    public string Authors { get; set; } = "";
    public int? FirstPublishYear { get; set; }

    /// <summary>
    /// The book's page in the catalogue. Built by the adapter from a checked identifier, always https. Null for a
    /// <c>Manual</c> book, kept as is on the review page because no catalogue has it.
    /// </summary>
    public string? InfoUrl { get; set; }
    public string? CoverUrl { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>The catalogue's description of the book, as plain text. Null until fetched, or when it has none.</summary>
    public string? Description { get; set; }

    /// <summary>Subjects or genres the catalogue files the book under, at most <see cref="BookDetails.MaxSubjects"/>.</summary>
    public List<string> Subjects { get; set; } = [];
    public int? PageCount { get; set; }

    /// <summary>The catalogue's average rating out of 5, from <see cref="RatingCount"/> of its users.</summary>
    public double? Rating { get; set; }
    public int? RatingCount { get; set; }

    /// <summary>
    /// When <c>BookDetailsFetcher</c> last asked the catalogue for the fields above. Null means never, so the book is
    /// due; that covers books found before details were fetched as well as new ones.
    /// </summary>
    public DateTime? DetailsCheckedAt { get; set; }

    /// <summary>The last ask failed, so it is tried again after <c>BookDetailsFetcher.RetryFailedAfter</c>.</summary>
    public bool DetailsFailed { get; set; }

    public List<BookMention> Mentions { get; set; } = [];
}
