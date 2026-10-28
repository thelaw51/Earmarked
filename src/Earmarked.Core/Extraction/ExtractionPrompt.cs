using Earmarked.Core.Data;

namespace Earmarked.Core.Extraction;

/// <summary>
/// The extraction prompt and the words it uses for each <see cref="MentionKind"/>. Any adapter that sends a prompt
/// uses these, so the version stored with a mention always says which instructions produced it.
/// </summary>
public static class ExtractionPrompt
{
    /// <summary>Stored with every mention. Changing the prompt means adding <c>book-mentions-v2.txt</c> and bumping this.</summary>
    public const string Version = "book-mentions-v1";

    /// <summary>The system prompt, read from the embedded <c>Prompts/book-mentions-v1.txt</c>.</summary>
    public static string Text { get; } = Load(Version);

    /// <summary>The kind names the prompt defines, in the order they're listed in the schema.</summary>
    public static readonly IReadOnlyDictionary<string, MentionKind> Kinds = new Dictionary<string, MentionKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["recommended"] = MentionKind.Recommended,
        ["own_book"] = MentionKind.OwnBook,
        ["discussed"] = MentionKind.Discussed,
        ["passing"] = MentionKind.Passing,
        ["criticised"] = MentionKind.Criticised,
    };

    /// <summary>
    /// The user message: context, then the excerpt inside <c>&lt;transcript&gt;</c> tags. Any tag of that name in the
    /// excerpt itself is defused, so transcript text can't close the block early.
    /// </summary>
    public static string UserMessage(ExtractionInput input) =>
        $"Podcast: {input.PodcastTitle}\nEpisode: {input.EpisodeTitle}\n\n<transcript>\n{Defuse(input.Text)}\n</transcript>";

    private static string Defuse(string text) =>
        text.Replace("<transcript>", "(transcript)", StringComparison.OrdinalIgnoreCase)
            .Replace("</transcript>", "(/transcript)", StringComparison.OrdinalIgnoreCase);

    private static string Load(string version)
    {
        using var stream = typeof(ExtractionPrompt).Assembly.GetManifestResourceStream($"Prompts/{version}.txt")
            ?? throw new InvalidOperationException($"Prompt {version} isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
