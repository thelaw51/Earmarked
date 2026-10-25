using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Feeds;
using PodcastBookRecs.Core.Pipeline;

namespace PodcastBookRecs.Tests;

public class EpisodeIntakeTests
{
    private static ParsedEpisode Ep(string key, int day, bool withTranscript = false) => new(
        key, key, key, new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, null,
        withTranscript ? [new TranscriptLink { Url = "https://x/t.vtt", Type = "text/vtt" }] : []);

    private static readonly HashSet<string> None = [];

    [Fact]
    public void First_poll_queues_newest_items_and_skips_the_rest()
    {
        ParsedEpisode[] feed = [Ep("old", 1), Ep("newest", 20, withTranscript: true), Ep("middle", 10)];

        var plan = EpisodeIntake.Plan(feed, None, isFirstPoll: true, backfill: 2);

        Assert.Equal(
            [("newest", EpisodeState.New), ("middle", EpisodeState.AwaitingTranscription), ("old", EpisodeState.Skipped)],
            plan.Select(p => (p.Episode.IdentityKey, p.State)));
    }

    [Fact]
    public void Later_polls_queue_every_new_item_and_ignore_known_ones()
    {
        ParsedEpisode[] feed = [Ep("a", 1), Ep("b", 2), Ep("c", 3)];

        var plan = EpisodeIntake.Plan(feed, new HashSet<string> { "b" }, isFirstPoll: false, backfill: 0);

        Assert.Equal(["a", "c"], plan.Select(p => p.Episode.IdentityKey));
        Assert.All(plan, p => Assert.Equal(EpisodeState.AwaitingTranscription, p.State));
    }

    private static Episode Stored(EpisodeState state, params string[] linkUrls) => new()
    {
        IdentityKey = "a",
        Title = "a",
        State = state,
        Attempts = 3,
        LastError = "old error",
        TranscriptLinks = [.. linkUrls.Select(u => new TranscriptLink { Url = u, Type = "text/vtt" })],
    };

    [Fact]
    public void Requeues_a_waiting_episode_when_the_feed_adds_a_transcript()
    {
        var episode = Stored(EpisodeState.AwaitingTranscription);

        Assert.True(EpisodeIntake.RefreshTranscriptLinks(episode, Ep("a", 1, withTranscript: true), DateTime.UtcNow));
        Assert.Equal(EpisodeState.New, episode.State);
        Assert.Single(episode.TranscriptLinks);
        Assert.Equal(0, episode.Attempts);
        Assert.Null(episode.LastError);
    }

    [Fact]
    public void Leaves_a_waiting_episode_alone_when_its_links_are_unchanged()
    {
        // Same links as before means they already failed; retrying every poll would loop forever.
        var episode = Stored(EpisodeState.AwaitingTranscription, "https://x/t.vtt");

        Assert.False(EpisodeIntake.RefreshTranscriptLinks(episode, Ep("a", 1, withTranscript: true), DateTime.UtcNow));
        Assert.Equal(EpisodeState.AwaitingTranscription, episode.State);
        Assert.Equal("old error", episode.LastError);
    }

    [Theory]
    [InlineData(EpisodeState.TranscriptFetched)]
    [InlineData(EpisodeState.Transcribed)]
    [InlineData(EpisodeState.Skipped)]
    [InlineData(EpisodeState.Transcribing)]
    public void Never_touches_episodes_past_the_waiting_states(EpisodeState state)
    {
        var episode = Stored(state);

        Assert.False(EpisodeIntake.RefreshTranscriptLinks(episode, Ep("a", 1, withTranscript: true), DateTime.UtcNow));
        Assert.Equal(state, episode.State);
        Assert.Empty(episode.TranscriptLinks);
    }

    [Fact]
    public void Updates_links_on_a_queued_episode_without_changing_its_state()
    {
        var episode = Stored(EpisodeState.New, "https://x/old.vtt");

        Assert.False(EpisodeIntake.RefreshTranscriptLinks(episode, Ep("a", 1, withTranscript: true), DateTime.UtcNow));
        Assert.Equal(EpisodeState.New, episode.State);
        Assert.Equal("https://x/t.vtt", Assert.Single(episode.TranscriptLinks).Url);
    }

    [Theory]
    [InlineData(EpisodeState.AwaitingTranscription)]
    [InlineData(EpisodeState.Transcribing)]
    public void Refreshes_the_audio_url_without_changing_state(EpisodeState state)
    {
        var episode = Stored(state);
        episode.EnclosureUrl = "https://cdn.test/old.mp3?token=expired";
        var latest = Ep("a", 1) with { EnclosureUrl = "https://cdn.test/new.mp3?token=fresh", EnclosureLength = 42 };

        Assert.True(EpisodeIntake.RefreshEnclosure(episode, latest, DateTime.UtcNow));
        Assert.Equal("https://cdn.test/new.mp3?token=fresh", episode.EnclosureUrl);
        Assert.Equal(42, episode.EnclosureLength);
        Assert.Equal(state, episode.State);
    }

    [Theory]
    [InlineData(EpisodeState.New)]
    [InlineData(EpisodeState.Transcribed)]
    [InlineData(EpisodeState.Skipped)]
    public void Leaves_the_audio_url_alone_outside_transcription(EpisodeState state)
    {
        var episode = Stored(state);
        episode.EnclosureUrl = "https://cdn.test/old.mp3";

        Assert.False(EpisodeIntake.RefreshEnclosure(episode, Ep("a", 1) with { EnclosureUrl = "https://cdn.test/new.mp3" }, DateTime.UtcNow));
        Assert.Equal("https://cdn.test/old.mp3", episode.EnclosureUrl);
    }

    [Fact]
    public void Zero_backfill_skips_everything_on_first_poll() =>
        Assert.All(EpisodeIntake.Plan([Ep("a", 1), Ep("b", 2)], None, true, 0), p => Assert.Equal(EpisodeState.Skipped, p.State));
}
