using Earmarked.Web.Services;

namespace Earmarked.Tests;

public class BookListParametersTests
{
    private static readonly Guid Show = Guid.Parse("01920000-0000-7000-8000-000000000001");

    [Fact]
    public void Query_values_round_trip()
    {
        var parsed = BookListParameters.Parse([Show.ToString()], "1", "2026-01-05", "2026-02-10", "  rivers ");

        Assert.Equal([Show], parsed.Podcasts);
        Assert.Equal((true, new DateOnly(2026, 1, 5), new DateOnly(2026, 2, 10), "rivers"), (parsed.RecommendedOnly, parsed.From, parsed.To, parsed.Text));
        var query = parsed.ToQuery();
        Assert.Equivalent(parsed, BookListParameters.Parse((string[]?)query["podcast"], (string?)query["rec"], (string?)query["from"], (string?)query["to"], (string?)query["q"]));
    }

    [Fact]
    public void Malformed_values_are_ignored_and_text_is_capped()
    {
        var parsed = BookListParameters.Parse(["not-a-guid", null, Show.ToString(), Show.ToString()], "yes", "05/01/2026", "9999-12-31", new string('x', 500));

        Assert.Equal([Show], parsed.Podcasts);
        Assert.Equal((false, (DateOnly?)null, (DateOnly?)null), (parsed.RecommendedOnly, parsed.From, parsed.To));
        Assert.Equal(BookListParameters.MaxTextLength, parsed.Text?.Length);
        Assert.True(BookListParameters.Parse(null, null, null, null, "   ").IsEmpty);
    }

    [Fact]
    public void Dates_cover_whole_local_days()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        var filter = new BookListParameters([], false, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 1), null).ToFilter(zone);

        Assert.Equal(new DateTime(2026, 2, 28, 22, 0, 0, DateTimeKind.Utc), filter.From);
        Assert.Equal(new DateTime(2026, 3, 1, 22, 0, 0, DateTimeKind.Utc).AddTicks(-1), filter.To);
    }
}
