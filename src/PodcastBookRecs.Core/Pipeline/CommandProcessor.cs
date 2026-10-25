using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>Runs commands the UI has queued in the <see cref="WorkerCommand"/> table, oldest first.</summary>
public sealed partial class CommandProcessor(
    IDbContextFactory<AppDbContext> dbFactory,
    FeedPoller poller,
    TimeProvider time,
    ILogger<CommandProcessor> logger)
{
    public async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        List<WorkerCommand> pending;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            pending = await db.WorkerCommands.Where(c => c.CompletedAt == null).OrderBy(c => c.Id).ToListAsync(cancellationToken);
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
            case WorkerCommandType.PollFeed when command.PodcastId is int podcastId:
                await poller.PollAsync(podcastId, cancellationToken);
                break;

            case WorkerCommandType.PollAllFeeds:
                List<int> ids;
                await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
                {
                    ids = await db.Podcasts.Where(p => p.Enabled).Select(p => p.Id).ToListAsync(cancellationToken);
                }

                foreach (var id in ids)
                {
                    await poller.PollAsync(id, cancellationToken);
                }

                break;

            case WorkerCommandType.RetryEpisode when command.EpisodeId is int episodeId:
                await RetryEpisodeAsync(episodeId, cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"Command {command.Type} is missing its target.");
        }
    }

    /// <summary>Puts an episode back at the start: New if it links a transcript, otherwise AwaitingTranscription.</summary>
    private async Task RetryEpisodeAsync(int episodeId, CancellationToken cancellationToken)
    {
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
        episode.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Command {Type} ({Id}) failed")]
    private partial void LogCommandFailed(Exception ex, WorkerCommandType type, int id);
}
