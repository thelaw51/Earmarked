using PodcastBookRecs.Core.BookLookup;

namespace PodcastBookRecs.Core.Data;

/// <summary>
/// A catalogue search a person asked for on the review page. The Web inserts it and the Worker runs it
/// (<c>CatalogSearchRunner</c>), because only the Worker talks to the catalogue: two processes pacing themselves
/// separately could break its rate limit. A picked result is copied from <see cref="Results"/>, never from the page.
/// </summary>
public class CatalogSearch
{
    public const int MaxErrorLength = 500;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Title { get; set; }
    public string? Author { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Set when the Worker has answered, with <see cref="Results"/> or with <see cref="Error"/>.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>A message our own code wrote for the person, never the catalogue's text.</summary>
    public string? Error { get; set; }

    /// <summary>The catalogue's candidates, as the adapter returned them. Null until the search completes.</summary>
    public List<CatalogBook>? Results { get; set; }
}
