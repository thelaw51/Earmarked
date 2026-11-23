using System.Text.RegularExpressions;
using Earmarked.Core.Data;

namespace Earmarked.Core.Extraction;

/// <summary>
/// The extraction prompt and the words it uses for each <see cref="MentionKind"/>. Any adapter that sends a prompt
/// uses these, so the version stored with a mention always says which instructions produced it.
/// </summary>
public static partial class ExtractionPrompt
{
    /// <summary>Stored with every mention. Changing the prompt means adding the next <c>book-mentions-vN.txt</c> and bumping this.</summary>
    public const string Version = "book-mentions-v2";

    /// <summary>
    /// Show notes are cut to this many characters. They are sent with every window, so they come out of the
    /// context left for transcript and answer; the guest and their book are usually named near the top.
    /// </summary>
    public const int MaxShowNotesLength = 1200;

    /// <summary>The system prompt, read from the embedded <c>Prompts/book-mentions-v2.txt</c>.</summary>
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
    /// The user message: the episode's context inside <c>&lt;context&gt;</c> tags, then the excerpt inside
    /// <c>&lt;transcript&gt;</c> tags. Either tag inside any of the feed's or transcript's text is defused, so that text
    /// can't close a block early or open a new one.
    /// </summary>
    public static string UserMessage(ExtractionInput input)
    {
        var context = input.Context;
        var notes = string.IsNullOrWhiteSpace(context.ShowNotes) ? "(none)" : Defuse(ShortNotes(context.ShowNotes));
        return $"<context>\nPodcast: {Defuse(context.PodcastTitle)}\nEpisode: {Defuse(context.EpisodeTitle)}\nShow notes: {notes}\n</context>\n\n"
            + $"<transcript>\n{Defuse(input.Text)}\n</transcript>";
    }

    /// <summary>Cuts notes to <see cref="MaxShowNotesLength"/>, at a word boundary when there is one, marking the cut.</summary>
    internal static string ShortNotes(string notes)
    {
        notes = notes.Trim();
        if (notes.Length <= MaxShowNotesLength)
        {
            return notes;
        }

        var cut = notes.LastIndexOf(' ', MaxShowNotesLength - 1);
        return notes[..(cut > 0 ? cut : MaxShowNotesLength - 1)] + "…";
    }

    /// <summary>Turns <c>&lt;transcript&gt;</c>, <c>&lt;/context &gt;</c> and the like into <c>(transcript)</c>, so they read as text.</summary>
    internal static string Defuse(string text) =>
        BlockTagPattern().Replace(text, m => $"({m.Groups["close"].Value}{m.Groups["name"].Value.ToLowerInvariant()})");

    private static string Load(string version)
    {
        using var stream = typeof(ExtractionPrompt).Assembly.GetManifestResourceStream($"Prompts/{version}.txt")
            ?? throw new InvalidOperationException($"Prompt {version} isn't embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    [GeneratedRegex(@"<\s*(?<close>/?)\s*(?<name>transcript|context)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagPattern();
}
