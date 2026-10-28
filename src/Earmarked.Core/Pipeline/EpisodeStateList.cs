using Microsoft.EntityFrameworkCore;
using Earmarked.Core.Data;
using Earmarked.Core.Feeds;

namespace Earmarked.Core.Pipeline;

/// <summary>
/// An episode in the Status page's per-state list. The podcast's title falls back to its feed's host. Never carries
/// <see cref="Episode.EnclosureUrl"/>, which holds a subscriber token on private feeds.
/// </summary>
public sealed record EpisodeStateRow(
    Guid Id,
    string Title,
    Guid PodcastId,
    string Podcast,
    EpisodeState State,
    DateTime? PublishedAt,
    DateTime UpdatedAt,
    int Attempts,
    DateTime? NextAttemptAt,
    DateTime? PrioritisedAt,
    string? LastError);

/// <summary>One page of <see cref="EpisodeStateList"/>: the rows shown and how many episodes matched in all.</summary>
public sealed record EpisodeStatePage(IReadOnlyList<EpisodeStateRow> Rows, int Total);

/// <summary>
/// Episodes across every podcast in one <see cref="EpisodeState"/>, so a count on the Status page can be opened to see
/// which episodes it is. Prioritised episodes come first (earliest request first), then the most recently changed.
/// That roughly matches what runs next, but it is not the exact claim order: grace periods, backoff and
/// <c>Extraction:WaitForTranscription</c> are left out.
/// </summary>
public static class EpisodeStateList
{
    /// <summary>The most rows one page loads. Skipped alone can run to thousands.</summary>
    public const int MaxRows = 500;

    /// <summary>Loads up to <paramref name="limit"/> episodes in <paramref name="state"/>, or in any state but Skipped when it is null.</summary>
    public static async Task<EpisodeStatePage> LoadAsync(
        AppDbContext db, EpisodeState? state, int limit, CancellationToken cancellationToken)
    {
        var episodes = state is { } s
            ? db.Episodes.Where(e => e.State == s)
            : db.Episodes.Where(e => e.State != EpisodeState.Skipped);
        var total = await episodes.CountAsync(cancellationToken);
        var rows = await episodes
            .OrderBy(e => e.PrioritisedAt == null)
            .ThenBy(e => e.PrioritisedAt)
            .ThenByDescending(e => e.UpdatedAt)
            .Take(Math.Clamp(limit, 1, MaxRows))
            .Select(e => new
            {
                e.Id,
                e.Title,
                e.PodcastFeedId,
                Podcast = e.PodcastFeed.Title,
                e.PodcastFeed.FeedUrl,
                e.State,
                e.PublishedAt,
                e.UpdatedAt,
                e.Attempts,
                e.NextAttemptAt,
                e.PrioritisedAt,
                e.LastError,
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return new EpisodeStatePage(
            rows.Select(r => new EpisodeStateRow(
                    r.Id, r.Title, r.PodcastFeedId, r.Podcast ?? FeedUrlRedactor.Redact(r.FeedUrl), r.State, r.PublishedAt,
                    r.UpdatedAt, r.Attempts, r.NextAttemptAt, r.PrioritisedAt, r.LastError))
                .ToList(),
            total);
    }

    /// <summary>How many episodes are in each state, for the Status page's bars and the list's state picker.</summary>
    public static async Task<IReadOnlyList<(EpisodeState State, int Count)>> CountAsync(AppDbContext db, CancellationToken cancellationToken) =>
        (await db.Episodes.GroupBy(e => e.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken))
            .OrderBy(x => x.Key)
            .Select(x => (x.Key, x.Count))
            .ToList();
}
