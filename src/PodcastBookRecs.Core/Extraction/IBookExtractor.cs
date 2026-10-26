using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Extraction;

/// <summary>Finds the books mentioned in a piece of transcript. Implementations call a language model.</summary>
public interface IBookExtractor
{
    /// <summary>Which adapter this is, stored with each mention, e.g. <c>OpenAiCompatible</c>.</summary>
    string Provider { get; }

    string Model { get; }

    /// <summary>The prompt's version, stored with each mention.</summary>
    string PromptVersion { get; }

    /// <summary>
    /// Returns the books the model found in <paramref name="input"/>. The results are unchecked: quotes may not be in
    /// the text. Throws <see cref="Backends.BackendUnavailableException"/> when the backend can't be reached, and
    /// <see cref="ExtractionFailedException"/> when this request failed.
    /// </summary>
    Task<IReadOnlyList<ExtractedMention>> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken);
}

/// <summary>One window of transcript, with the context that helps the model (the episode title usually names the guest).</summary>
public sealed record ExtractionInput(string PodcastTitle, string EpisodeTitle, string Text);

/// <summary>A book as the model reported it, before its quote has been checked.</summary>
public sealed record ExtractedMention(string Title, string? Author, string Quote, MentionKind Kind, double Confidence);

/// <summary>The extraction request failed in a way that counts against the episode (timeout, bad answer, server error).</summary>
public sealed class ExtractionFailedException(string message, Exception? innerException = null) : Exception(message, innerException);
