using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Tests;

public class TranscriptLinkSelectorTests
{
    [Theory]
    [InlineData("text/vtt", "https://x/t", TranscriptFormat.Vtt)]
    [InlineData("application/x-subrip", "https://x/t", TranscriptFormat.Srt)]
    [InlineData("application/srt", "https://x/t", TranscriptFormat.Srt)]
    [InlineData("application/json; charset=utf-8", "https://x/t", TranscriptFormat.Json)]
    [InlineData("TEXT/HTML", "https://x/t", TranscriptFormat.Html)]
    [InlineData("text/plain", "https://x/t", TranscriptFormat.PlainText)]
    [InlineData(null, "https://x/ep.srt?token=1", TranscriptFormat.Srt)]
    [InlineData("application/octet-stream", "https://x/ep.vtt", TranscriptFormat.Vtt)]
    [InlineData(null, "https://x/ep", TranscriptFormat.Unknown)]
    public void Detects_format_from_type_then_extension(string? type, string url, TranscriptFormat expected) =>
        Assert.Equal(expected, TranscriptLinkSelector.DetectFormat(type, url));

    [Fact]
    public void Prefers_vtt_then_srt_then_json_then_html()
    {
        TranscriptLink[] links =
        [
            new() { Url = "https://x/a.html", Type = "text/html" },
            new() { Url = "https://x/a.json", Type = "application/json" },
            new() { Url = "https://x/a.srt", Type = "application/x-subrip" },
        ];

        Assert.Equal(TranscriptFormat.Srt, TranscriptLinkSelector.SelectBest(links)!.Value.Format);
        Assert.Equal(TranscriptFormat.Json, TranscriptLinkSelector.SelectBest(links[..2])!.Value.Format);
    }

    [Fact]
    public void Returns_null_when_no_format_is_known() =>
        Assert.Null(TranscriptLinkSelector.SelectBest([new TranscriptLink { Url = "https://x/a.pdf", Type = "application/pdf" }]));
}
