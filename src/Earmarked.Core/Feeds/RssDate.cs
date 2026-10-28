using System.Globalization;

namespace Earmarked.Core.Feeds;

/// <summary>
/// Lenient RFC 822 date parsing for <c>pubDate</c>. Real feeds use named US zones (EST, PDT), drop the weekday,
/// or get the weekday wrong, so the strict parser alone rejects too many of them.
/// </summary>
public static class RssDate
{
    private static readonly Dictionary<string, string> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+0000", ["UTC"] = "+0000", ["GMT"] = "+0000", ["Z"] = "+0000",
        ["EST"] = "-0500", ["EDT"] = "-0400", ["CST"] = "-0600", ["CDT"] = "-0500",
        ["MST"] = "-0700", ["MDT"] = "-0600", ["PST"] = "-0800", ["PDT"] = "-0700",
        ["BST"] = "+0100",
    };

    private static readonly string[] Formats =
    [
        "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz",
        "d MMM yyyy HH:mm:ss", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
    ];

    /// <summary>Returns the date in UTC, or null when it can't be parsed.</summary>
    public static DateTime? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0 && comma <= 10)
        {
            text = text[(comma + 1)..].Trim();
        }

        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0 && ZoneOffsets.TryGetValue(text[(lastSpace + 1)..], out var offset))
        {
            text = text[..lastSpace] + " " + offset;
        }

        if (text.Length > 5 && (text[^5] is '+' or '-') && text[^4..].All(char.IsAsciiDigit))
        {
            text = text[..^2] + ":" + text[^2..];
        }

        return DateTimeOffset.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed)
            || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed)
            ? parsed.UtcDateTime
            : null;
    }
}
