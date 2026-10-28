using System.Globalization;
using Earmarked.Core.Transcripts;

namespace Earmarked.Web.Services;

/// <summary>
/// Links to a moment in an episode's transcript. A link carries the time (<c>#at-754.601</c>), not a row number, so the
/// book page needn't load transcripts; the episode page turns the time into its row (<c>t-{index}</c>) once it has the
/// segments. A mention without a time links to the episode's list of books (<c>#mentions</c>).
/// </summary>
public static class TranscriptLinks
{
    public const string MentionsId = "mentions";

    private const string TimePrefix = "at-";

    // No episode runs for a week; the cap also keeps TimeSpan.FromSeconds clear of its overflow limit.
    private static readonly double MaxSeconds = TimeSpan.FromDays(7).TotalSeconds;

    /// <summary>The element ID of transcript row <paramref name="index"/>.</summary>
    public static string RowId(int index) => $"t-{index}";

    public static string Href(Guid episodeId, TimeSpan? at) => $"episodes/{episodeId}#{Fragment(at)}";

    /// <summary>
    /// The fragment for a moment. Rounded up to the millisecond: a mention's time is the start of its segment, and
    /// rounding down could land just before it, on the previous row.
    /// </summary>
    public static string Fragment(TimeSpan? at) => at is { } time
        ? TimePrefix + (Math.Ceiling(time.TotalMilliseconds) / 1000).ToString("0.###", CultureInfo.InvariantCulture)
        : MentionsId;

    /// <summary>
    /// The element a URL's fragment points at on the episode page: the row being spoken at that time (the last one
    /// starting at or before it), or the list of books. Null for anything else, including times before the first row.
    /// </summary>
    public static string? TargetId(string uri, IReadOnlyList<TranscriptSegment> segments)
    {
        var hash = uri.IndexOf('#', StringComparison.Ordinal);
        var fragment = hash < 0 ? "" : uri[(hash + 1)..];
        if (fragment == MentionsId)
        {
            return MentionsId;
        }

        if (!fragment.StartsWith(TimePrefix, StringComparison.Ordinal)
            || !double.TryParse(fragment[TimePrefix.Length..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || seconds > MaxSeconds)
        {
            return null;
        }

        return RowAt(TimeSpan.FromSeconds(seconds), segments);
    }

    /// <summary>The ID of the row being spoken at <paramref name="at"/>, or null if no timed row starts by then.</summary>
    public static string? RowAt(TimeSpan at, IReadOnlyList<TranscriptSegment> segments)
    {
        var index = -1;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Start is { } start && start <= at)
            {
                index = i;
            }
        }

        return index < 0 ? null : RowId(index);
    }
}
