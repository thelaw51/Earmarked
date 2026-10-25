using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Core.Transcription;

/// <summary>
/// Turns audio into timed segments. Implementations live under <c>Providers/Transcription/</c>, one folder per
/// provider, and the pipeline only ever sees this interface and the types below.
/// </summary>
public interface ITranscriptionService
{
    /// <summary>The configured provider name, recorded on each transcript.</summary>
    string Provider { get; }

    /// <summary>The model name, recorded on each transcript.</summary>
    string Model { get; }

    /// <summary>
    /// Transcribes one audio file. Times in the result are relative to the start of this file.
    /// Throws <see cref="Backends.BackendUnavailableException"/> when the backend can't be reached, and
    /// <see cref="TranscriptionFailedException"/> when it was reached but this request failed.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken);
}

public sealed record TranscriptionResult(IReadOnlyList<TranscriptSegment> Segments, string? Language, TimeSpan? Duration);

/// <summary>The backend answered but couldn't transcribe this audio, or the request timed out. Counts against the episode.</summary>
public sealed class TranscriptionFailedException(string message, Exception? innerException = null) : Exception(message, innerException);
