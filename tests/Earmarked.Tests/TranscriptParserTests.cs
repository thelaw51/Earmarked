using Earmarked.Core.Transcripts;

namespace Earmarked.Tests;

public class TranscriptParserTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Parses_vtt_cues_voices_and_inline_tags()
    {
        var segments = TranscriptParser.Parse(Fixtures.Read("transcript.vtt"), TranscriptFormat.Vtt);

        Assert.Equal(
            [
                new TranscriptSegment(S(1), S(4.5), "Alice", "Welcome back to the show."),
                new TranscriptSegment(S(4.5), S(9.25), "Bob Smith", "I finally read The Lantern Index by Mara Quill & loved it."),
                new TranscriptSegment(S(62), S(65), null, "No speaker on this cue."),
            ],
            segments);
    }

    [Fact]
    public void Parses_srt_with_crlf_line_endings()
    {
        var segments = TranscriptParser.Parse(Fixtures.Read("transcript.srt"), TranscriptFormat.Srt);

        Assert.Equal(3, segments.Count);
        Assert.Equal(new TranscriptSegment(S(4.5), S(9.25), null, "I finally read The Lantern Index by Mara Quill."), segments[1]);
        Assert.Equal(new TimeSpan(0, 1, 2, 3, 40), segments[2].Start);
    }

    [Fact]
    public void Parses_podcast_namespace_json_and_skips_empty_bodies()
    {
        var segments = TranscriptParser.Parse(Fixtures.Read("transcript.json"), TranscriptFormat.Json);

        Assert.Equal(
            [
                new TranscriptSegment(S(1), S(4.5), "Alice", "Welcome back to the show."),
                new TranscriptSegment(S(4.5), S(9.25), "Bob", "I finally read The Lantern Index."),
                new TranscriptSegment(S(62), null, null, "No speaker or end time here."),
            ],
            segments);
    }

    [Fact]
    public void Parses_html_carrying_speaker_and_time_forward()
    {
        var segments = TranscriptParser.Parse(Fixtures.Read("transcript.html"), TranscriptFormat.Html);

        Assert.Equal(
            [
                new TranscriptSegment(S(1), null, "Alice", "Welcome back to the show."),
                new TranscriptSegment(S(4), null, "Bob", "I finally read The Lantern Index & loved it."),
                new TranscriptSegment(S(4), null, "Bob", "Then I read it again."),
            ],
            segments);
    }

    [Fact]
    public void Splits_plain_text_into_paragraphs_without_times()
    {
        var segments = TranscriptParser.Parse("First paragraph\nstill first.\n\nSecond.", TranscriptFormat.PlainText);

        Assert.Equal([new(null, null, null, "First paragraph still first."), new(null, null, null, "Second.")], segments);
    }

    [Fact]
    public void Pulls_speaker_labels_out_of_plain_text()
    {
        var segments = TranscriptParser.Parse(Fixtures.Read("transcript.txt"), TranscriptFormat.PlainText);

        Assert.Equal(
            [
                new TranscriptSegment(null, null, "Ben", "Welcome back to the show."),
                new TranscriptSegment(null, null, "David", "Thanks, Ben. Today we are talking about The Lantern Index."),
                new TranscriptSegment(null, null, null, "INTRO"),
                new TranscriptSegment(null, null, "Dr. Jane Smith", "I read it twice: once for the plot, once for the footnotes."),
                new TranscriptSegment(null, null, "Ben", "That is all for today."),
            ],
            segments);
    }

    [Fact]
    public void Leaves_prose_alone_when_only_a_few_paragraphs_look_labelled()
    {
        const string prose = "We talked about the book.\n\nNote: the author was not on the show.\n\nThen we moved on to the news.";

        var segments = TranscriptParser.Parse(prose, TranscriptFormat.PlainText);

        Assert.All(segments, s => Assert.Null(s.Speaker));
        Assert.Equal("Note: the author was not on the show.", segments[1].Text);
    }

    [Fact]
    public void Does_not_treat_times_or_lowercase_words_as_speakers()
    {
        const string text = "10:30 we started recording.\n\nsoftware: it is everywhere.\n\nBen: Hello.";

        var segments = TranscriptParser.Parse(text, TranscriptFormat.PlainText);

        Assert.Equal([null, null, null], segments.Select(s => s.Speaker));
    }

    [Theory]
    [InlineData("WEBVTT\n\n", TranscriptFormat.Vtt)]
    [InlineData("{\"version\":\"1.0.0\",\"segments\":[]}", TranscriptFormat.Json)]
    [InlineData("{\"not\":\"a transcript\"}", TranscriptFormat.Json)]
    [InlineData("{ broken", TranscriptFormat.Json)]
    [InlineData("   ", TranscriptFormat.PlainText)]
    public void Throws_on_unusable_content(string content, TranscriptFormat format) =>
        Assert.Throws<TranscriptFormatException>(() => TranscriptParser.Parse(content, format));

    [Theory]
    [InlineData("00:00:01.500", 1.5)]
    [InlineData("01:02:03,250", 3723.25)]
    [InlineData("02:03.000", 123)]
    [InlineData("0:04", 4)]
    public void Parses_timestamps(string value, double seconds) => Assert.Equal(S(seconds), TranscriptParser.ParseTimestamp(value));

    [Theory]
    [InlineData("soon")]
    [InlineData("12")]
    [InlineData("a:b:c")]
    public void Rejects_bad_timestamps(string value) => Assert.Null(TranscriptParser.ParseTimestamp(value));
}
