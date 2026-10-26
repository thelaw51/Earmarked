using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Web.Services;

namespace PodcastBookRecs.Tests;

public class DisplayTests
{
    [Theory]
    [InlineData("https://example.com/cover.jpg", true)]
    [InlineData("http://example.com/cover.jpg", true)]
    [InlineData("HTTP://example.com/cover.jpg", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("ftp://example.com/cover.jpg", false)]
    [InlineData("/relative/cover.jpg", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Feed_artwork_allows_only_absolute_http_and_https(string? url, bool allowed)
    {
        Assert.Equal(allowed ? url : null, Display.HttpOrHttpsOrNull(url));
    }

    [Theory]
    [InlineData("https://example.com/cover.jpg", true)]
    [InlineData("http://example.com/cover.jpg", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Catalogue_links_allow_only_https(string? url, bool allowed)
    {
        Assert.Equal(allowed ? url : null, Display.HttpsOrNull(url));
    }

    [Theory]
    [InlineData(EpisodeState.New, 0, false)]
    [InlineData(EpisodeState.AwaitingTranscription, 0, false)]
    [InlineData(EpisodeState.Transcribing, 0, false)]
    [InlineData(EpisodeState.Failed, 0, true)]
    [InlineData(EpisodeState.TranscriptFetched, 1, false)]
    [InlineData(EpisodeState.Transcribed, 1, false)]
    [InlineData(EpisodeState.Extracting, 1, false)]
    [InlineData(EpisodeState.ExtractionFailed, 1, true)]
    [InlineData(EpisodeState.Extracted, 3, false)]
    [InlineData(EpisodeState.Skipped, 0, false)]
    public void Progress_marks_the_step_in_progress_or_failed(EpisodeState state, int completed, bool failed)
    {
        Assert.Equal((completed, failed), Display.Progress(state));
    }
}
