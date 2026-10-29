using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Earmarked.Core.Text;

/// <summary>
/// Turns HTML from feeds and catalogues (show notes, book descriptions) into plain text for storing and showing. The
/// result is only ever rendered as text, so this is for readability, not safety: tags go, block ends become line
/// breaks, entities are decoded, runs of blank lines collapse, and the text is capped. The input is untrusted and can
/// be large, so it is cut to <see cref="InputFactor"/> times the cap first and every pattern runs in linear time
/// (<see cref="RegexOptions.NonBacktracking"/>).
/// </summary>
public static partial class PlainText
{
    /// <summary>How much more markup than text is read: show notes are mostly tags and links.</summary>
    private const int InputFactor = 8;

    /// <summary>
    /// Plain text from <paramref name="html"/>, at most <paramref name="max"/> UTF-16 units, or null when nothing but
    /// markup and whitespace is left. A cut text ends with "…", and never splits a surrogate pair.
    /// </summary>
    public static string? FromHtml(string? html, int max)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = html.Length > max * InputFactor ? html[..(max * InputFactor)] : html;
        text = Invisible().Replace(text, "");
        text = Break().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');

        var lines = new StringBuilder();
        var blank = false;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = Spaces().Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                blank = lines.Length > 0;
                continue;
            }

            if (lines.Length > 0)
            {
                lines.Append(blank ? "\n\n" : "\n");
            }

            lines.Append(line);
            blank = false;
        }

        return lines.Length == 0 ? null : Cap(lines.ToString(), max);
    }

    /// <summary>
    /// <paramref name="value"/> cut to at most <paramref name="max"/> UTF-16 units with "…" at the end, one shorter if
    /// the cut would split a surrogate pair.
    /// </summary>
    public static string Cap(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        var end = Math.Max(0, max - 1);
        if (end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        return value[..end].TrimEnd() + "…";
    }

    /// <summary>Elements whose content is never text a person reads.</summary>
    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>|<style\b[^>]*>.*?</style\s*>|<head\b[^>]*>.*?</head\s*>|<template\b[^>]*>.*?</template\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex Invisible();

    /// <summary>Tags that end a line: breaks, and the end of paragraphs, list items, headings and the like.</summary>
    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|h[1-6]|tr|blockquote|pre|ul|ol)\s*>|<(p|li|ul|ol)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Break();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.NonBacktracking)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.NonBacktracking)]
    private static partial Regex Spaces();
}
