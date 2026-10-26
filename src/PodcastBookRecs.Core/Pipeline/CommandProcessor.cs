using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>Runs commands the UI has queued in the <see cref="WorkerCommand"/> table, oldest first.</summary>
public sealed partial class CommandProcessor(
    IDbContextFactory<AppDbContext> dbFactory,
    FeedPoller poller,
    EpisodeStateLock stateLock,
    TranscriptionWorkspace workspace,
    TimeProvider time,
    ILogger<CommandProcessor> logger)
{
    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        List<WorkerCommand> pending;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            pending = await db.WorkerCommands.Where(c => c.CompletedAt == null).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(cancellationToken);
        }

        foreach (var command in pending)
        {
            string? error = null;
            try
            {
                await RunAsync(command, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
                LogCommandFailed(ex, command.Type, command.Id);
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await db.WorkerCommands.Where(c => c.Id == command.Id).ExecuteUpdateAsync(
                s => s.SetProperty(c => c.CompletedAt, time.GetUtcNow().UtcDateTime).SetProperty(c => c.Error, error),
                cancellationToken);
        }
    }

    private async Task RunAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        switch (command.Type)
        {
            case WorkerCommandType.PollFeed when command.PodcastFeedId is Guid podcastId:
                await poller.PollAsync(podcastId, cancellationToken);
                break;

            case WorkerCommandType.PollAllFeeds:
                List<Guid> ids;
                await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
                {
                    ids = await db.PodcastFeeds.Where(p => p.Enabled).Select(p => p.Id).ToListAsync(cancellationToken);
                }

                foreach (var id in ids)
                {
                    await poller.PollAsync(id, cancellationToken);
                }

                break;

            case WorkerCommandType.RetryEpisode when command.EpisodeId is Guid episodeId:
                await RetryEpisodeAsync(episodeId, cancellationToken);
                break;

            case WorkerCommandType.ReextractEpisode when command.EpisodeId is Guid episodeId:
                await ReextractEpisodeAsync(episodeId, cancellationToken);
                break;

            case WorkerCommandType.PrioritiseEpisode when command.EpisodeId is Guid episodeId:
                await UpdateQueueAsync(episodeId, prioritise: true, cancellationToken);
                break;

            case WorkerCommandType.IncludeEpisode when command.EpisodeId is Guid episodeId:
                await UpdateQueueAsync(episodeId, prioritise: false, cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"Command {command.Type} is missing its target.");
        }
    }

    /// <summary>
    /// Puts an episode back at the start: New if it links a transcript, otherwise AwaitingTranscription. Deletes any
    /// partial transcription work so a retried transcription starts over.
    /// </summary>
    private async Task RetryEpisodeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        using var _ = await stateLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var episode = await db.Episodes.Include(e => e.Transcript).SingleOrDefaultAsync(e => e.Id == episodeId, cancellationToken)
            ?? throw new InvalidOperationException($"Episode {episodeId} not found.");

        if (episode.Transcript is not null)
        {
            db.Transcripts.Remove(episode.Transcript);
        }

        episode.State = episode.TranscriptLinks.Count > 0 ? EpisodeState.New : EpisodeState.AwaitingTranscription;
        episode.Attempts = 0;
        episode.NextAttemptAt = null;
        episode.LastError = null;
        episode.PrioritisedAt = null;
        episode.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        workspace.Delete(episodeId);
    }

    /// <summary>
    /// Includes a skipped episode, and with <paramref name="prioritise"/> also moves it to the front of whichever queue
    /// it's in, clearing any retry backoff. Including polls the episode's feed straight after, because a skipped
    /// episode's audio and transcript links were never refreshed and signed links expire.
    /// </summary>
    private async Task UpdateQueueAsync(Guid episodeId, bool prioritise, CancellationToken cancellationToken)
    {
        bool included;
        Guid podcastId;
        using (await stateLock.AcquireAsync(cancellationToken))
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var episode = await db.Episodes.SingleOrDefaultAsync(e => e.Id == episodeId, cancellationToken)
                ?? throw new InvalidOperationException($"Episode {episodeId} not found.");
            included = episode.State == EpisodeState.Skipped;
            if (!included && (!prioritise || !QueuedStates.Contains(episode.State)))
            {
                throw new InvalidOperationException(prioritise
                    ? $"Episode {episodeId} can't be prioritised while it's {episode.State}."
                    : $"Episode {episodeId} isn't skipped, so there's nothing to include.");
            }

            var now = time.GetUtcNow().UtcDateTime;
            if (included)
            {
                episode.State = episode.TranscriptLinks.Count > 0 ? EpisodeState.New : EpisodeState.AwaitingTranscription;
                episode.Attempts = 0;
                episode.LastError = null;
            }

            if (prioritise)
            {
                episode.PrioritisedAt ??= now;
                episode.NextAttemptAt = null;
            }

            episode.UpdatedAt = now;
            podcastId = episode.PodcastFeedId;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (included)
        {
            await poller.PollAsync(podcastId, cancellationToken);
        }
    }

    /// <summary>States an episode waits in for its next step, where priority changes what runs next.</summary>
    public static readonly EpisodeState[] QueuedStates =
        [EpisodeState.New, EpisodeState.AwaitingTranscription, EpisodeState.TranscriptFetched, EpisodeState.Transcribed];

    /// <summary>
    /// Deletes an episode's mentions and queues it for extraction again from its stored transcript. Only for episodes
    /// that finished extraction or gave up: one being extracted now would race the extractor.
    /// </summary>
    private async Task ReextractEpisodeAsync(Guid episodeId, CancellationToken cancellationToken)
    {
        using var _ = await stateLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var episode = await db.Episodes.Include(e => e.Transcript).SingleOrDefaultAsync(e => e.Id == episodeId, cancellationToken)
            ?? throw new InvalidOperationException($"Episode {episodeId} not found.");
        if (episode.Transcript is null || episode.State is not (EpisodeState.Extracted or EpisodeState.ExtractionFailed))
        {
            throw new InvalidOperationException($"Episode {episodeId} can't be re-extracted while it's {episode.State}.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.BookMentions.Where(m => m.EpisodeId == episodeId).ExecuteDeleteAsync(cancellationToken);
        episode.State = episode.Transcript.Source == TranscriptSource.Transcribed ? EpisodeState.Transcribed : EpisodeState.TranscriptFetched;
        episode.Attempts = 0;
        episode.NextAttemptAt = null;
        episode.LastError = null;
        episode.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {Type} ({Id}) failed")]
    private partial void LogCommandFailed(Exception ex, WorkerCommandType type, Guid id);
}
