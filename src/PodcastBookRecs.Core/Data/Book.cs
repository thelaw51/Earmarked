namespace PodcastBookRecs.Core.Data;

/// <summary>
/// A published book, as a catalogue knows it. Mentions from any number of episodes and podcasts point at one book, which
/// is what lets books be ranked. Rows are only ever added: a book outlives the mentions that found it.
/// </summary>
public class Book
{
    public const int MaxTitleLength = 300;
    public const int MaxAuthorsLength = 500;
    public const int MaxUrlLength = 300;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The catalogue it came from, e.g. <c>OpenLibrary</c>. With <see cref="CatalogId"/>, identifies the book.</summary>
    public required string Provider { get; set; }

    /// <summary>The catalogue's own identifier for the work (all editions together), e.g. <c>OL27513W</c>.</summary>
    public required string CatalogId { get; set; }
    public required string Title { get; set; }

    /// <summary>Authors as the catalogue lists them, joined with ", ". Empty when the catalogue has none.</summary>
    public string Authors { get; set; } = "";
    public int? FirstPublishYear { get; set; }

    /// <summary>The book's page in the catalogue. Built by the adapter from a checked identifier, always https.</summary>
    public required string InfoUrl { get; set; }
    public string? CoverUrl { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<BookMention> Mentions { get; set; } = [];
}
