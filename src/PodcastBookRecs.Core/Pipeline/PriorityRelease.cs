using Microsoft.EntityFrameworkCore;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>
/// Clears "Do this next" from episodes that have their transcript, for a Worker with no book extraction. Without it an
/// episode's work ends at the transcript, and the extractor, which normally clears the flag, never runs. The Worker
/// runs this on a loop only while extraction is off, so it also clears episodes prioritised before it was turned off.
/// </summary>
public sealed class PriorityRelease(IDbContextFactory<AppDbContext> dbFactory, EpisodeStateLock stateLock)
{
    /// <summary>The states an episode stops in when there's no extraction.</summary>
    public static readonly EpisodeState[] FinishedWithoutExtraction = [EpisodeState.TranscriptFetched, EpisodeState.Transcribed];

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        // "Do this next" sets the flag under the lock, so clearing it takes the lock too.
        using var _ = await stateLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Episodes
            .Where(e => e.PrioritisedAt != null && FinishedWithoutExtraction.Contains(e.State))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.PrioritisedAt, (DateTime?)null), cancellationToken);
    }
}
