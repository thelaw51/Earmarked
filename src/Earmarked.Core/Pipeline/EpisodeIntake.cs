using Earmarked.Core.Data;
using Earmarked.Core.Feeds;
using Earmarked.Core.Transcripts;

namespace Earmarked.Core.Pipeline;

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

    /// <summary>States whose transcript links are still worth refreshing: nothing has been fetched or transcribed yet.</summary>
    public static readonly EpisodeState[] RefreshableStates = [EpisodeState.New, EpisodeState.AwaitingTranscription];

    /// <summary>
    /// Updates a stored episode's transcript links from the latest feed, for shows that add transcripts after release.
    /// An AwaitingTranscription episode goes back to New only when its set of link URLs has changed and now includes a
    /// usable format, so links that already failed aren't retried on every poll. Returns true if the episode was requeued.
    /// </summary>
    public static bool RefreshTranscriptLinks(Episode episode, ParsedEpisode latest, DateTime now)
    {
        if (!RefreshableStates.Contains(episode.State))
        {
            return false;
        }

        var stored = episode.TranscriptLinks.Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        if (stored.SetEquals(latest.TranscriptLinks.Select(l => l.Url)))
        {
            return false;
        }

        episode.TranscriptLinks = [.. latest.TranscriptLinks];
        episode.UpdatedAt = now;
        if (episode.State != EpisodeState.AwaitingTranscription || InitialState(latest) != EpisodeState.New)
        {
            return false;
        }

        episode.State = EpisodeState.New;
        episode.Attempts = 0;
        episode.NextAttemptAt = null;
        episode.LastError = null;
        return true;
    }

    /// <summary>States whose audio URL is refreshed from the feed: signed links (e.g. Patreon) expire before a long backlog reaches them.</summary>
    public static readonly EpisodeState[] EnclosureRefreshableStates = [EpisodeState.AwaitingTranscription, EpisodeState.Transcribing];

    /// <summary>Updates the stored audio URL, type and length when the feed's differ. Never changes the episode's state.</summary>
    public static bool RefreshEnclosure(Episode episode, ParsedEpisode latest, DateTime now)
    {
        if (!EnclosureRefreshableStates.Contains(episode.State) || latest.EnclosureUrl is null || latest.EnclosureUrl == episode.EnclosureUrl)
        {
            return false;
        }

        episode.EnclosureUrl = latest.EnclosureUrl;
        episode.EnclosureType = latest.EnclosureType;
        episode.EnclosureLength = latest.EnclosureLength;
        episode.UpdatedAt = now;
        return true;
    }

    /// <summary>New if the item links a transcript in a format we can parse, otherwise AwaitingTranscription.</summary>
    public static EpisodeState InitialState(ParsedEpisode episode) =>
        TranscriptLinkSelector.SelectBest(episode.TranscriptLinks) is null ? EpisodeState.AwaitingTranscription : EpisodeState.New;
}
