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

    [Fact]
    public void Zero_backfill_skips_everything_on_first_poll() =>
        Assert.All(EpisodeIntake.Plan([Ep("a", 1), Ep("b", 2)], None, true, 0), p => Assert.Equal(EpisodeState.Skipped, p.State));
}
