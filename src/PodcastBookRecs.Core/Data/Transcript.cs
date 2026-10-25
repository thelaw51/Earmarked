using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Core.Data;

/// <summary>A stored transcript, kept so extraction can be re-run without re-fetching or re-transcribing.</summary>
public class Transcript
{
    public int Id { get; set; }
    public int EpisodeId { get; set; }
    public Episode Episode { get; set; } = null!;

    public TranscriptSource Source { get; set; }
    public TranscriptFormat Format { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<TranscriptSegment> Segments { get; set; } = [];
}

public enum TranscriptSource
{
    Feed = 0,
    Whisper = 1,
}
