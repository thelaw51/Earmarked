using PodcastBookRecs.Core.Transcripts;
using PodcastBookRecs.Web.Services;

namespace PodcastBookRecs.Tests;

public class TranscriptLinksTests
{
    private static readonly TranscriptSegment[] Segments =
    [
        new(TimeSpan.FromSeconds(1.5), null, null, "One."),
        new(TimeSpan.FromSeconds(754.6004), null, null, "Two."),
        new(TimeSpan.FromSeconds(760), null, null, "Three."),
    ];

    [Fact]
    public void A_mentions_time_leads_back_to_its_own_row()
    {
        // 754.6004 s would round down to 754.600 and land on row 0; rounding up keeps it on row 1.
        var href = TranscriptLinks.Href(Guid.Empty, Segments[1].Start);

        Assert.EndsWith("#at-754.601", href, StringComparison.Ordinal);
        Assert.Equal("t-1", TranscriptLinks.TargetId("https://app.test/" + href, Segments));
    }

    [Theory]
    [InlineData("https://app.test/episodes/x#at-759", "t-1")]
    [InlineData("https://app.test/episodes/x#at-9000", "t-2")]
    [InlineData("https://app.test/episodes/x#at-1", null)]
    [InlineData("https://app.test/episodes/x#mentions", "mentions")]
    [InlineData("https://app.test/episodes/x#at--5", null)]
    [InlineData("https://app.test/episodes/x#at-1e309", null)]
    [InlineData("https://app.test/episodes/x#at-922337203685.4775", null)]
    [InlineData("https://app.test/episodes/x#t-1", null)]
    [InlineData("https://app.test/episodes/x", null)]
    public void A_fragment_resolves_to_the_row_spoken_at_that_time(string uri, string? target)
    {
        Assert.Equal(target, TranscriptLinks.TargetId(uri, Segments));
    }

    [Fact]
    public void An_untimed_mention_links_to_the_list_of_books()
    {
        Assert.EndsWith("#mentions", TranscriptLinks.Href(Guid.Empty, null), StringComparison.Ordinal);
        Assert.Null(TranscriptLinks.TargetId("https://app.test/episodes/x#at-3", [new(null, null, null, "Untimed.")]));
    }
}
