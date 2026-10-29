using System.Xml;
using Earmarked.Core.Feeds;

namespace Earmarked.Tests;

public class FeedParserTests
{
    private readonly ParsedFeed _feed = FeedParser.Parse(Fixtures.Read("feed.xml"));

    [Fact]
    public void Reads_channel_details()
    {
        Assert.Equal("The Imaginary Bookshelf", _feed.Title);
        Assert.Equal("Example Productions", _feed.Author);
        Assert.Equal("https://example.com/cover.jpg", _feed.ImageUrl);
    }

    [Fact]
    public void Uses_guid_then_enclosure_as_identity_and_drops_duplicates_and_unidentifiable_items()
    {
        Assert.Equal(["ep-003", "ep-002", "https://cdn.example.com/ep1.mp3"], _feed.Episodes.Select(e => e.IdentityKey));
        Assert.Null(_feed.Episodes[2].ItemGuid);
    }

    [Fact]
    public void Reads_episode_fields()
    {
        var ep = _feed.Episodes[0];
        Assert.Equal("Episode 3: Rivers of Paper", ep.Title);
        Assert.Equal(new DateTime(2026, 10, 22, 5, 0, 0, DateTimeKind.Utc), ep.PublishedAt);
        Assert.Equal(TimeSpan.FromSeconds(34 * 60 + 2), ep.Duration);
        Assert.Equal(32679392, ep.EnclosureLength);
        Assert.Equal("audio/mpeg", ep.EnclosureType);
    }

    [Fact]
    public void Reads_podcast_transcript_tags()
    {
        var links = _feed.Episodes[0].TranscriptLinks;
        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/ep3.vtt", links[1].Url);
        Assert.Equal("text/vtt", links[1].Type);
        Assert.Equal("en", links[1].Language);
        Assert.Empty(_feed.Episodes[1].TranscriptLinks);
    }

    [Fact]
    public void Reads_show_notes_as_plain_text_preferring_the_full_content()
    {
        Assert.Equal("This week: paper & rivers.\n\nBooks mentioned:\n\nThe Lighthouse Keeper's Almanac by Ines Calder", _feed.Episodes[0].Description);
        Assert.Equal("Only an iTunes summary.", _feed.Episodes[1].Description);
        Assert.Null(_feed.Episodes[2].Description);
    }

    [Theory]
    [InlineData("34:02", 2042)]
    [InlineData("1:02:03", 3723)]
    [InlineData("2535", 2535)]
    [InlineData("2535.5", 2535.5)]
    public void Parses_itunes_duration(string value, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), FeedParser.ParseDuration(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("about an hour")]
    [InlineData("1:2:3:4")]
    public void Rejects_bad_durations(string? value) => Assert.Null(FeedParser.ParseDuration(value));

    [Fact]
    public void Rejects_documents_that_are_not_rss() =>
        Assert.Throws<FeedFormatException>(() => FeedParser.Parse("<feed xmlns=\"http://www.w3.org/2005/Atom\"></feed>"));

    [Fact]
    public void Does_not_resolve_external_entities()
    {
        const string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY xxe SYSTEM "file:///etc/hosts">]>
            <rss version="2.0"><channel><title>&xxe;</title></channel></rss>
            """;
        Assert.Throws<XmlException>(() => FeedParser.Parse(xml));
    }
}
