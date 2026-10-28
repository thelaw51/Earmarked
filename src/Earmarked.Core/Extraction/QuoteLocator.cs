using System.Text;
using System.Text.RegularExpressions;

namespace Earmarked.Core.Extraction;

/// <summary>Where a quote was found: the first and last-plus-one word of the matching passage.</summary>
public sealed record QuoteMatch(int StartWord, int EndWord);

/// <summary>
/// Checks that a quote the model returned is really in the transcript, and finds where. Small models invent
/// mentions, and an invented one rarely comes with a quote that's actually there. Comparison ignores case,
/// punctuation and <c>[mm:ss]</c> markers. An exact word-for-word match wins; otherwise a passage of the same length
/// sharing at least 80% of the quote's words counts, which absorbs small slips such as "Calder" for "Caldor".
/// </summary>
public static partial class QuoteLocator
{
    public const double FuzzyThreshold = 0.8;

    /// <summary>Quotes are compared on at most this many words, which bounds the fuzzy search's cost.</summary>
    internal const int MaxQuoteWords = 60;

    /// <summary>Quotes shorter than this must match exactly: a fuzzy match on two or three words means little.</summary>
    private const int MinFuzzyWords = 4;

    /// <summary>Looks for <paramref name="quote"/> in words <paramref name="from"/> up to (not including) <paramref name="to"/>.</summary>
    public static QuoteMatch? Find(TranscriptWords words, string quote, int from, int to)
    {
        var target = Tokens(MarkerPattern().Replace(quote, " ")).Take(MaxQuoteWords).ToArray();
        if (target.Length == 0)
        {
            return null;
        }

        // Normalised transcript tokens in range, with the word index each came from (punctuation-only words drop out).
        var tokens = new List<(string Token, int Word)>();
        for (var i = from; i < to; i++)
        {
            var token = Normalise(words.Words[i]);
            if (token.Length > 0)
            {
                tokens.Add((token, i));
            }
        }

        if (tokens.Count < target.Length)
        {
            return null;
        }

        for (var start = 0; start + target.Length <= tokens.Count; start++)
        {
            var all = true;
            for (var j = 0; j < target.Length && all; j++)
            {
                all = tokens[start + j].Token == target[j];
            }

            if (all)
            {
                return new QuoteMatch(tokens[start].Word, tokens[start + target.Length - 1].Word + 1);
            }
        }

        return target.Length >= MinFuzzyWords ? FindFuzzy(tokens, target) : null;
    }

    /// <summary>The best same-length passage by shared words (counting repeats), if it reaches the threshold. Earliest wins ties.</summary>
    private static QuoteMatch? FindFuzzy(List<(string Token, int Word)> tokens, string[] target)
    {
        var wanted = target.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var bestScore = 0;
        var bestStart = -1;
        for (var start = 0; start + target.Length <= tokens.Count; start++)
        {
            var remaining = new Dictionary<string, int>(wanted, StringComparer.Ordinal);
            var score = 0;
            for (var j = 0; j < target.Length; j++)
            {
                if (remaining.TryGetValue(tokens[start + j].Token, out var left) && left > 0)
                {
                    remaining[tokens[start + j].Token] = left - 1;
                    score++;
                }
            }

            if (score > bestScore)
            {
                (bestScore, bestStart) = (score, start);
            }
        }

        return bestStart >= 0 && bestScore >= FuzzyThreshold * target.Length
            ? new QuoteMatch(tokens[bestStart].Word, tokens[bestStart + target.Length - 1].Word + 1)
            : null;
    }

    private static IEnumerable<string> Tokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalise).Where(t => t.Length > 0);

    /// <summary>Lower case, letters and digits only: "Caldor," and "caldor" compare equal, and "don't" becomes "dont".</summary>
    internal static string Normalise(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\[\d{1,2}(:\d{2}){1,2}\]")]
    private static partial Regex MarkerPattern();
}
