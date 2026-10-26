using System.Globalization;
using System.Text;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Core.Extraction;

/// <summary>A transcript flattened into words, each remembering which segment it came from.</summary>
public sealed class TranscriptWords
{
    private readonly IReadOnlyList<TranscriptSegment> _segments;

    public TranscriptWords(IReadOnlyList<TranscriptSegment> segments)
    {
        _segments = segments;
        var words = new List<string>();
        var owners = new List<int>();
        for (var i = 0; i < segments.Count; i++)
        {
            foreach (var word in segments[i].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add(word);
                owners.Add(i);
            }
        }

        Words = words;
        SegmentOf = owners;
    }

    public IReadOnlyList<string> Words { get; }

    /// <summary>For each word, the index of its segment.</summary>
    public IReadOnlyList<int> SegmentOf { get; }

    public int Count => Words.Count;

    /// <summary>When the segment holding word <paramref name="index"/> starts. Null for transcripts without timings.</summary>
    public TimeSpan? StartOf(int index) => _segments[SegmentOf[index]].Start;

    /// <summary>The words from <paramref name="start"/> up to (not including) <paramref name="end"/>, joined by spaces.</summary>
    public string Span(int start, int end) => string.Join(' ', Words.Skip(start).Take(end - start));
}

/// <summary>A run of words sent to the extractor in one request.</summary>
public sealed record TranscriptWindow(int StartWord, int EndWord, string Text);

/// <summary>
/// Splits a transcript into overlapping windows by word count. Windows go by words, not time, because published
/// plain-text transcripts have no timings and a minute of talk varies a lot in length.
/// </summary>
public static class TranscriptWindows
{
    public static IReadOnlyList<TranscriptWindow> Split(TranscriptWords words, int windowWords, int overlapWords)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowWords, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(overlapWords, windowWords);

        var windows = new List<TranscriptWindow>();
        for (var start = 0; start < words.Count; start += windowWords - overlapWords)
        {
            var end = Math.Min(start + windowWords, words.Count);
            windows.Add(new TranscriptWindow(start, end, Format(words, start, end)));
            if (end == words.Count)
            {
                break;
            }
        }

        return windows;
    }

    /// <summary>Starts a new line, marked <c>[mm:ss]</c> when the segment has a time, wherever the segment changes.</summary>
    private static string Format(TranscriptWords words, int start, int end)
    {
        var text = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            if (i == start || words.SegmentOf[i] != words.SegmentOf[i - 1])
            {
                if (i > start)
                {
                    text.Append('\n');
                }

                if (words.StartOf(i) is TimeSpan at)
                {
                    text.Append('[').Append(Marker(at)).Append("] ");
                }
            }
            else
            {
                text.Append(' ');
            }

            text.Append(words.Words[i]);
        }

        return text.ToString();
    }

    private static string Marker(TimeSpan at) =>
        at.TotalHours >= 1 ? at.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : at.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}
