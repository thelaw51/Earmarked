namespace Earmarked.Core.Transcripts;

/// <summary>One timed piece of a transcript. Times are null when the source format has none (plain text, some HTML).</summary>
public sealed record TranscriptSegment(TimeSpan? Start, TimeSpan? End, string? Speaker, string Text);

/// <summary>Transcript formats in order of preference: lower values carry better timing.</summary>
public enum TranscriptFormat
{
    Vtt = 0,
    Srt = 1,
    Json = 2,
    Html = 3,
    PlainText = 4,

    /// <summary>Produced by our own speech-to-text service rather than published by the podcast.</summary>
    SpeechToText = 5,
    Unknown = 99,
}
