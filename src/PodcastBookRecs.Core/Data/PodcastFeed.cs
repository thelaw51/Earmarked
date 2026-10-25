namespace PodcastBookRecs.Core.Data;

/// <summary>A podcast feed the Worker polls for new episodes.</summary>
public class PodcastFeed
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The feed URL as entered. May contain a private subscriber token, so never log or display it unredacted.</summary>
    public required string FeedUrl { get; set; }

    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? ImageUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime AddedAt { get; set; }

    /// <summary>Set after the first successful poll. Null means the next poll applies the backfill window.</summary>
    public DateTime? FirstPolledAt { get; set; }

    public DateTime? LastPolledAt { get; set; }
    public string? LastPollError { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }

    /// <summary>
    /// True once any episode of this feed has carried a transcript link. Such shows sometimes add a transcript days
    /// after release, so their new episodes wait a grace period before being transcribed.
    /// </summary>
    public bool PublishesTranscripts { get; set; }

    public List<Episode> Episodes { get; set; } = [];
}
