using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Extraction;

/// <summary>A mention whose quote was found in the transcript.</summary>
public sealed record LocatedMention(ExtractedMention Mention, QuoteMatch Match);

/// <summary>
/// Combines the mentions of one book within an episode. Overlapping windows report the same mention twice, and the
/// model sometimes repeats a book inside one window.
/// </summary>
public static class MentionMerger
{
    /// <summary>
    /// Groups by normalised title. Each group keeps its earliest quote, the highest confidence, the first author
    /// given, and the strongest kind (own book, then recommended, criticised, discussed, passing).
    /// </summary>
    public static IReadOnlyList<LocatedMention> Merge(IEnumerable<LocatedMention> mentions) =>
        mentions
            .GroupBy(m => TitleKey(m.Mention.Title), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.MinBy(m => m.Match.StartWord)!;
                var merged = first.Mention with
                {
                    Author = group.OrderBy(m => m.Match.StartWord).Select(m => m.Mention.Author).FirstOrDefault(a => a is not null),
                    Kind = group.Select(m => m.Mention.Kind).MaxBy(Strength),
                    Confidence = group.Max(m => m.Mention.Confidence),
                };
                return first with { Mention = merged };
            })
            .OrderBy(m => m.Match.StartWord)
            .ToList();

    /// <summary>Lower case, letters and digits only, without a leading "the", "a" or "an".</summary>
    internal static string TitleKey(string title)
    {
        var words = title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(QuoteLocator.Normalise).Where(w => w.Length > 0).ToList();
        if (words.Count > 1 && words[0] is "the" or "a" or "an")
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    private static int Strength(MentionKind kind) => kind switch
    {
        MentionKind.OwnBook => 4,
        MentionKind.Recommended => 3,
        MentionKind.Criticised => 2,
        MentionKind.Discussed => 1,
        _ => 0,
    };
}
