namespace PodcastBookRecs.Core.Data;

/// <summary>A request from the UI for the Worker to do something now. The Worker polls this table.</summary>
public class WorkerCommand
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public WorkerCommandType Type { get; set; }
    public Guid? PodcastFeedId { get; set; }
    public Guid? EpisodeId { get; set; }

    /// <summary>The mentions' lookup key, for <see cref="WorkerCommandType.ReviewDecided"/>.</summary>
    public string? LookupKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public enum WorkerCommandType
{
    PollFeed = 0,
    PollAllFeeds = 1,
    RetryEpisode = 2,

    /// <summary>Deletes an episode's book mentions and extracts them again from the stored transcript.</summary>
    ReextractEpisode = 3,

    /// <summary>Moves an episode to the front of its queue. A skipped episode is included first.</summary>
    PrioritiseEpisode = 4,

    /// <summary>Queues an episode skipped by the backfill window, in its normal place.</summary>
    IncludeEpisode = 5,

    /// <summary>
    /// A review decision for <see cref="WorkerCommand.LookupKey"/> was made, changed or undone: mentions with that key are
    /// matched again, and the matcher applies whatever decision is now stored.
    /// </summary>
    ReviewDecided = 6,
}
