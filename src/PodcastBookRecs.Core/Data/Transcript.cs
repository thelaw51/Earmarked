using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Core.Data;

/// <summary>A stored transcript, kept so extraction can be re-run without re-fetching or re-transcribing.</summary>
public class Transcript
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EpisodeId { get; set; }
    public Episode Episode { get; set; } = null!;

    public TranscriptSource Source { get; set; }
    public TranscriptFormat Format { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<TranscriptSegment> Segments { get; set; } = [];
}

/// <summary>
/// Where a transcript came from. Deliberately provider-neutral: which transcription service and model produced
/// a transcript is recorded separately, so switching provider doesn't change stored values.
/// </summary>
public enum TranscriptSource
{
    /// <summary>Published by the podcast in a <c>&lt;podcast:transcript&gt;</c> tag.</summary>
    Feed = 0,

    /// <summary>Produced by our own speech-to-text service.</summary>
    Transcribed = 1,
}
