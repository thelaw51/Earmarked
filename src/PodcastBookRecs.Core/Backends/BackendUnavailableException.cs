namespace PodcastBookRecs.Core.Backends;

/// <summary>
/// An external backend (transcription today, extraction later) can't be reached at all. Callers treat this as
/// "try again later", never as a failure of the job being worked on, so it never uses up an episode's attempts.
/// </summary>
public sealed class BackendUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);
