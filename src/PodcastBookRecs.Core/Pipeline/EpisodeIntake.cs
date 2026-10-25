using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Feeds;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>Decides which feed items are new and what state each new episode starts in.</summary>
public static class EpisodeIntake
{
    /// <summary>
    /// Returns the items not already stored, each with its starting state. On a feed's first poll only the newest
    /// <paramref name="backfill"/> items are queued and the rest are Skipped; after that every new item is queued.
    /// </summary>
    public static IReadOnlyList<(ParsedEpisode Episode, EpisodeState State)> Plan(
        IReadOnlyList<ParsedEpisode> parsed, IReadOnlySet<string> existingKeys, bool isFirstPoll, int backfill)
    {
        var fresh = parsed.Where(e => !existingKeys.Contains(e.IdentityKey));
        if (isFirstPoll)
        {
            fresh = fresh.OrderByDescending(e => e.PublishedAt ?? DateTime.MinValue);
        }

        return fresh
            .Select((e, i) => (e, isFirstPoll && i >= backfill ? EpisodeState.Skipped : InitialState(e)))
            .ToList();
    }

    /// <summary>New if the item links a transcript in a format we can parse, otherwise AwaitingTranscription.</summary>
    public static EpisodeState InitialState(ParsedEpisode episode) =>
        TranscriptLinkSelector.SelectBest(episode.TranscriptLinks) is null ? EpisodeState.AwaitingTranscription : EpisodeState.New;
}
