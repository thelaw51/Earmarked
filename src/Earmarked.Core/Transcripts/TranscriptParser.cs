using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Earmarked.Core.Transcripts;

/// <summary>Parses published transcripts into timed segments. Throws <see cref="TranscriptFormatException"/> on content it can't use.</summary>
public static partial class TranscriptParser
{
    public static List<TranscriptSegment> Parse(string content, TranscriptFormat format)
    {
        var segments = format switch
        {
            TranscriptFormat.Vtt => ParseCues(content, isVtt: true),
            TranscriptFormat.Srt => ParseCues(content, isVtt: false),
            TranscriptFormat.Json => ParseJson(content),
            TranscriptFormat.Html => ParseHtml(content),
            TranscriptFormat.PlainText => ParsePlainText(content),
            _ => throw new TranscriptFormatException($"Unsupported transcript format {format}."),
        };

        return segments.Count > 0 ? segments : throw new TranscriptFormatException("Transcript contained no text.");
    }

    /// <summary>
    /// VTT and SRT share a shape: blocks separated by blank lines, each with an optional id, a
    /// <c>start --&gt; end</c> line and text. VTT adds a header, NOTE/STYLE/REGION blocks and inline tags.
    /// </summary>
    private static List<TranscriptSegment> ParseCues(string content, bool isVtt)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var block in SplitBlocks(content))
        {
            var lines = block.Split('\n');
            var timingIndex = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                continue;
            }

            var times = lines[timingIndex].Split("-->", StringSplitOptions.TrimEntries);
            var start = ParseTimestamp(times[0]);
            var end = ParseTimestamp(times[1].Split(' ', 2)[0]);
            if (start is null)
            {
                continue;
            }

            string? speaker = null;
            var text = string.Join(' ', lines.Skip(timingIndex + 1).Where(l => l.Length > 0));
            if (isVtt)
            {
                var voice = VttVoiceTag().Match(text);
                speaker = voice.Success ? voice.Groups[1].Value.Trim() : null;
                text = VttTag().Replace(text, "");
            }

            text = Normalise(WebUtility.HtmlDecode(text));
            if (text.Length > 0)
            {
                segments.Add(new TranscriptSegment(start, end, speaker, text));
            }
        }

        return segments;
    }

    /// <summary>Podcasting 2.0 JSON: <c>{ "version": "1.0.0", "segments": [{ "speaker", "startTime", "endTime", "body" }] }</c>, times in seconds.</summary>
    private static List<TranscriptSegment> ParseJson(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("segments", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                throw new TranscriptFormatException("JSON transcript has no segments array.");
            }

            var segments = new List<TranscriptSegment>();
            foreach (var s in array.EnumerateArray())
            {
                var body = s.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? Normalise(b.GetString()!) : "";
                if (body.Length == 0)
                {
                    continue;
                }

                var speaker = s.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                segments.Add(new TranscriptSegment(Seconds(s, "startTime"), Seconds(s, "endTime"), string.IsNullOrWhiteSpace(speaker) ? null : speaker.Trim(), body));
            }

            return segments;
        }
        catch (JsonException ex)
        {
            throw new TranscriptFormatException($"Invalid JSON transcript: {ex.Message}");
        }

        static TimeSpan? Seconds(JsonElement segment, string name)
        {
            if (!segment.TryGetProperty(name, out var value))
            {
                return null;
            }

            double? seconds = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
                _ => null,
            };
            return seconds is >= 0 ? TimeSpan.FromSeconds(seconds.Value) : null;
        }
    }

    /// <summary>
    /// The spec's HTML form is low fidelity: <c>&lt;cite&gt;Speaker:&lt;/cite&gt; &lt;time&gt;0:00&lt;/time&gt; &lt;p&gt;text&lt;/p&gt;</c>.
    /// Each paragraph becomes a segment carrying the most recent speaker and time seen before it.
    /// </summary>
    private static List<TranscriptSegment> ParseHtml(string content)
    {
        var segments = new List<TranscriptSegment>();
        string? speaker = null;
        TimeSpan? time = null;
        foreach (Match m in HtmlElement().Matches(content))
        {
            var inner = Normalise(WebUtility.HtmlDecode(AnyTag().Replace(m.Groups["body"].Value, " ")));
            switch (m.Groups["tag"].Value.ToLowerInvariant())
            {
                case "cite":
                    speaker = inner.TrimEnd(':').Trim() is { Length: > 0 } name ? name : null;
                    break;
                case "time":
                    time = ParseTimestamp(inner);
                    break;
                case "p" when inner.Length > 0:
                    segments.Add(new TranscriptSegment(time, null, speaker, inner));
                    break;
            }
        }

        return segments.Count > 0 ? segments : ParsePlainText(AnyTag().Replace(content, "\n"));
    }

    /// <summary>
    /// One segment per paragraph. Many plain-text transcripts start each paragraph with <c>Name:</c>; when at least half
    /// the paragraphs do, that name becomes the segment's speaker. The threshold keeps a stray "Note:" in ordinary prose
    /// from being read as a speaker. Paragraphs without a prefix (e.g. "INTRO") keep no speaker rather than a guessed one.
    /// </summary>
    private static List<TranscriptSegment> ParsePlainText(string content)
    {
        var paragraphs = SplitBlocks(WebUtility.HtmlDecode(content)).Select(Normalise).Where(t => t.Length > 0).ToList();
        var labelled = paragraphs.Count(p => SpeakerPrefix().IsMatch(p));
        var hasSpeakers = labelled > 0 && labelled * 2 >= paragraphs.Count;

        return paragraphs
            .Select(p => hasSpeakers && SpeakerPrefix().Match(p) is { Success: true } m
                ? new TranscriptSegment(null, null, m.Groups["name"].Value, m.Groups["text"].Value)
                : new TranscriptSegment(null, null, null, p))
            .ToList();
    }

    /// <summary>Parses <c>hh:mm:ss.fff</c>, <c>mm:ss.fff</c> or SRT's <c>hh:mm:ss,fff</c>.</summary>
    internal static TimeSpan? ParseTimestamp(string value)
    {
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }

        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
        {
            return null;
        }

        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    private static IEnumerable<string> SplitBlocks(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Trim('﻿').Split("\n\n").Select(b => b.Trim('\n')).Where(b => b.Length > 0);

    private static string Normalise(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"<v(?:\.[^\s>]+)*\s+([^>]+)>")]
    private static partial Regex VttVoiceTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex VttTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"<(?<tag>cite|time|p)\b[^>]*>(?<body>.*?)</\k<tag>\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlElement();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>A leading speaker label: one to four capitalised words, a colon, then the spoken text.</summary>
    [GeneratedRegex(@"^(?<name>\p{Lu}[\w.'’-]*(?: \p{Lu}[\w.'’-]*){0,3}):\s+(?<text>\S.*)$")]
    private static partial Regex SpeakerPrefix();
}

/// <summary>Thrown when a transcript can't be parsed. The episode then falls back to the transcription service.</summary>
public sealed class TranscriptFormatException(string message) : Exception(message);
