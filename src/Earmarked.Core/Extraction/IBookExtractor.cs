using Earmarked.Core.Data;

namespace Earmarked.Core.Extraction;

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

/// <summary>
/// What the model is told about the episode, the same for every window. The episode title and show notes usually name
/// the guest and their book, which resolves "my new book"; the podcast's name keeps the show itself off the list.
/// All three come from the feed, so they are untrusted. <see cref="ShowNotes"/> is plain text, or null when the feed has none.
/// </summary>
public sealed record ExtractionContext(string PodcastTitle, string EpisodeTitle, string? ShowNotes);

/// <summary>One window of transcript, with the episode's context.</summary>
public sealed record ExtractionInput(ExtractionContext Context, string Text);

/// <summary>
/// A book as the model reported it, before its quote has been checked. <see cref="Evidence"/> is the model's few words
/// saying why it is a book ("a book called", "she wrote"); a mention without any is dropped. It isn't stored.
/// </summary>
public sealed record ExtractedMention(string Title, string? Author, string Quote, MentionKind Kind, double Confidence, string? Evidence = null);

/// <summary>The extraction request failed in a way that counts against the episode (timeout, bad answer, server error).</summary>
public sealed class ExtractionFailedException(string message, Exception? innerException = null) : Exception(message, innerException);
