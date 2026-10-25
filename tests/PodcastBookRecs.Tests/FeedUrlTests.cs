using PodcastBookRecs.Core.Feeds;

namespace PodcastBookRecs.Tests;

public class FeedUrlTests
{
    [Theory]
    [InlineData("https://private.example.com/content/eyJsecret-token.rss?t=123", "https://private.example.com/…")]
    [InlineData("https://feeds.example.com/", "https://feeds.example.com/")]
    [InlineData("not a url", "(invalid URL)")]
    [InlineData(null, "(invalid URL)")]
    public void Redaction_shows_only_the_host(string? url, string expected) => Assert.Equal(expected, FeedUrlRedactor.Redact(url));

    [Theory]
    [InlineData("https://example.com/feed.xml", true)]
    [InlineData(" http://example.com/feed ", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://example.com/feed", false)]
    [InlineData("example.com/feed", false)]
    [InlineData("", false)]
    public void Accepts_only_http_and_https(string url, bool valid) => Assert.Equal(valid, FeedUrl.IsValid(url));
}
