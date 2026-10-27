using System.Globalization;
using PodcastBookRecs.Core.Books;

namespace PodcastBookRecs.Web.Services;

/// <summary>
/// The Books page's filters as they appear in the query string (<c>?podcast=…&amp;rec=1&amp;from=…&amp;to=…&amp;q=…</c>),
/// so a filtered list can be bookmarked and an export link carries the same filters. Dates are local calendar days.
/// Query text is untrusted: anything that doesn't parse is ignored, and the lists and text are capped.
/// </summary>
public sealed record BookListParameters(IReadOnlyList<Guid> Podcasts, bool RecommendedOnly, DateOnly? From, DateOnly? To, string? Text)
{
    public const int MaxTextLength = 200;
    public const int MaxPodcasts = 50;

    private const string DateFormat = "yyyy-MM-dd";

    public static readonly BookListParameters None = new([], false, null, null, null);

    public bool IsEmpty => Podcasts.Count == 0 && !RecommendedOnly && From is null && To is null && Text is null;

    /// <summary>Reads the raw query values, dropping anything malformed.</summary>
    public static BookListParameters Parse(IEnumerable<string?>? podcast, string? rec, string? from, string? to, string? q)
    {
        var podcasts = (podcast ?? [])
            .Select(p => Guid.TryParse(p, CultureInfo.InvariantCulture, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .Take(MaxPodcasts)
            .ToList();
        var text = q?.Trim();
        return new BookListParameters(
            podcasts,
            rec is "1" or "true",
            ParseDate(from),
            ParseDate(to),
            string.IsNullOrEmpty(text) ? null : text.Length <= MaxTextLength ? text : text[..MaxTextLength]);
    }

    /// <summary>
    /// The query values to put in a URL, for <c>NavigationManager.GetUriWithQueryParameters</c>. Null removes a key, so
    /// clearing a filter takes it out of the URL.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ToQuery() => new Dictionary<string, object?>
    {
        ["podcast"] = Podcasts.Count == 0 ? null : Podcasts.Select(p => p.ToString()).ToArray(),
        ["rec"] = RecommendedOnly ? "1" : null,
        ["from"] = From?.ToString(DateFormat, CultureInfo.InvariantCulture),
        ["to"] = To?.ToString(DateFormat, CultureInfo.InvariantCulture),
        ["q"] = Text,
    };

    /// <summary>
    /// The filter for <see cref="BookListQuery"/>: <see cref="From"/> from the start of that day and <see cref="To"/> to
    /// the end of that day, both in <paramref name="zone"/>, as UTC.
    /// </summary>
    public BookFilter ToFilter(TimeZoneInfo zone) => new(
        Podcasts,
        RecommendedOnly,
        From is { } from ? StartOfDayUtc(from, zone) : null,
        To is { } to ? StartOfDayUtc(to.AddDays(1), zone).AddTicks(-1) : null,
        Text);

    // Dates near DateOnly's limits would overflow when shifted to UTC or to the next day, so only sane years count.
    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year is >= 1900 and <= 2999
            ? date
            : null;

    private static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        // A few zones move their clocks at midnight, so the day can start later than 00:00.
        var local = date.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
