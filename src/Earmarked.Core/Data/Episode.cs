namespace Earmarked.Core.Data;

/// <summary>One item from a podcast feed, tracked as a job moving through <see cref="EpisodeState"/>.</summary>
public class Episode
{
    public const int MaxDescriptionLength = 8000;

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

    /// <summary>
    /// The show notes from the feed, as plain text (<see cref="Text.PlainText"/>). Feed content, so untrusted: shown only
    /// as text. Filled for episodes stored before notes were kept on the feed's next full download.
    /// </summary>
    public string? Description { get; set; }

    public List<TranscriptLink> TranscriptLinks { get; set; } = [];

    public EpisodeState State { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime DiscoveredAt { get; set; }

    /// <summary>
    /// When "Do this next" was asked for. Prioritised episodes go to the front of the transcript, transcription and
    /// extraction queues, earliest request first, skipping the published-transcript grace period. Cleared when the
    /// episode finishes extraction or gives up.
    /// </summary>
    public DateTime? PrioritisedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Transcript? Transcript { get; set; }

    public List<BookMention> Mentions { get; set; } = [];
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

    /// <summary>Claimed by the extractor; the transcript is being searched for book mentions. Never refreshed by the poller.</summary>
    Extracting = 5,

    /// <summary>Book mentions have been extracted (possibly none).</summary>
    Extracted = 6,

    /// <summary>Older than the backfill window when the feed was first added. Stored so later polls don't treat it as new.</summary>
    Skipped = 10,

    /// <summary>Transcription ran out of attempts.</summary>
    Failed = 20,

    /// <summary>Extraction ran out of attempts. The transcript is kept, so it can be re-run without transcribing again.</summary>
    ExtractionFailed = 21,
}

/// <summary>A <c>&lt;podcast:transcript&gt;</c> entry from the feed item.</summary>
public class TranscriptLink
{
    public required string Url { get; set; }
    public string? Type { get; set; }
    public string? Language { get; set; }
    public string? Rel { get; set; }
}
