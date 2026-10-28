using Earmarked.Core.Data;
using Earmarked.Web.Services;

namespace Earmarked.Tests;

public sealed class EpisodeStateParameterTests
{
    [Theory]
    [InlineData("Transcribing", EpisodeState.Transcribing)]
    [InlineData("failed", EpisodeState.Failed)]
    [InlineData(" EXTRACTIONFAILED ", EpisodeState.ExtractionFailed)]
    public void A_state_name_parses_in_any_case(string value, EpisodeState expected) =>
        Assert.Equal(expected, EpisodeStateParameter.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("999")]
    [InlineData("Transcribing,Failed")]
    [InlineData("Whisper")]
    public void Anything_but_a_state_name_is_ignored(string? value) =>
        Assert.Null(EpisodeStateParameter.Parse(value));

    [Fact]
    public void Links_round_trip_through_the_parser()
    {
        Assert.Equal("episodes", EpisodeStateParameter.Link(null));
        foreach (var state in Enum.GetValues<EpisodeState>())
        {
            var query = EpisodeStateParameter.Link(state).Split("state=")[1];
            Assert.Equal(state, EpisodeStateParameter.Parse(query));
        }
    }
}
