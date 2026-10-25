using PodcastBookRecs.Core.Feeds;

namespace PodcastBookRecs.Tests;

public class RssDateTests
{
    [Theory]
    [InlineData("Thu, 22 Oct 2026 05:00:00 GMT", "2026-10-22T05:00:00")]
    [InlineData("Thu, 22 Oct 2026 05:00:00 +0000", "2026-10-22T05:00:00")]
    [InlineData("Mon, 19 Oct 2026 05:00:00 -0400", "2026-10-19T09:00:00")]
    [InlineData("Thu, 15 Oct 2026 05:00:00 EDT", "2026-10-15T09:00:00")]
    [InlineData("Fri, 16 Oct 2026 05:00:00 PST", "2026-10-16T13:00:00")]
    [InlineData("16 Oct 2026 05:00 +0100", "2026-10-16T04:00:00")]
    [InlineData("Wed, 22 Oct 2026 05:00:00 GMT", "2026-10-22T05:00:00")]
    [InlineData("2026-10-22T05:00:00Z", "2026-10-22T05:00:00")]
    public void Parses_real_world_formats_to_utc(string value, string expectedUtc)
    {
        var parsed = RssDate.TryParse(value);
        Assert.Equal(DateTime.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed!.Value.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("last Tuesday")]
    public void Returns_null_for_unparseable_dates(string? value) => Assert.Null(RssDate.TryParse(value));
}
