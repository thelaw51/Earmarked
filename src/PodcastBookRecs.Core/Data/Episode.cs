namespace PodcastBookRecs.Core.Data;

/// <summary>One item from a podcast feed, tracked as a job moving through <see cref="EpisodeState"/>.</summary>
public class Episode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid PodcastFeedId { get; set; }
    public PodcastFeed PodcastFeed { get; set; } = null!;

    /// <summary>Dedupe key within the podcast: the item GUID, or the enclosure URL when the item has no GUID.</summary>
    public required string IdentityKey { get; set; }

    public string? ItemGuid { get; set; }
    public required string Title { get; set; }
    public DateTime? PublishedAt { get; set; }
    public TimeSpan? Duration { get; set; }
    public string? EnclosureUrl { get; set; }
    public string? EnclosureType { get; set; }
    public long? EnclosureLength { get; set; }
    public string? Link { get; set; }

    public List<TranscriptLink> TranscriptLinks { get; set; } = [];

    public EpisodeState State { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime DiscoveredAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Transcript? Transcript { get; set; }
}

/// <summary>Where an episode is in the pipeline.</summary>
public enum EpisodeState
{
    /// <summary>Has a published transcript link waiting to be fetched.</summary>
    New = 0,

    /// <summary>No usable published transcript; waits for the transcription service.</summary>
    AwaitingTranscription = 1,

    TranscriptFetched = 2,
    Transcribed = 3,

    /// <summary>Claimed by the transcriber; audio is being downloaded, split or transcribed. Never refreshed by the poller.</summary>
    Transcribing = 4,

    /// <summary>Older than the backfill window when the feed was first added. Stored so later polls don't treat it as new.</summary>
    Skipped = 10,

    Failed = 20,
}

/// <summary>A <c>&lt;podcast:transcript&gt;</c> entry from the feed item.</summary>
public class TranscriptLink
{
    public required string Url { get; set; }
    public string? Type { get; set; }
    public string? Language { get; set; }
    public string? Rel { get; set; }
}
