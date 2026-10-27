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
            catch (CommandRefusedException ex)
            {
                error = ex.Message;
                LogCommandRefused(command.Type, command.Id, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = FailedError;
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

            case WorkerCommandType.IncludeSkippedEpisodes when command.PodcastFeedId is Guid podcastId && command.Count is >= 1 and <= MaxIncludeCount:
                await IncludeSkippedAsync(podcastId, command.Count.Value, cancellationToken);
                break;

            case WorkerCommandType.ReviewDecided when command.LookupKey is { Length: > 0 and <= MaxLookupKeyLength } key:
                await RematchKeyAsync(key, cancellationToken);
                break;

            default:
                throw new CommandRefusedException($"Command {command.Type} is missing its target.");
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
            ?? throw new CommandRefusedException("That episode no longer exists.");

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
                ?? throw new CommandRefusedException("That episode no longer exists.");
            included = episode.State == EpisodeState.Skipped;
            if (!included && (!prioritise || !QueuedStates.Contains(episode.State)))
            {
                throw new CommandRefusedException(prioritise
                    ? $"This episode can't be prioritised while it's {episode.State}."
                    : "This episode isn't skipped, so there's nothing to include.");
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

    /// <summary>The most skipped episodes one command includes, the same ceiling as <c>Polling:BackfillEpisodes</c>.</summary>
    public const int MaxIncludeCount = 1000;

    /// <summary>
    /// Includes a podcast's newest <paramref name="count"/> skipped episodes in one go, then polls its feed once to
    /// refresh their links, as <see cref="UpdateQueueAsync"/> does for one episode.
    /// </summary>
    private async Task IncludeSkippedAsync(Guid podcastId, int count, CancellationToken cancellationToken)
    {
        using (await stateLock.AcquireAsync(cancellationToken))
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var episodes = await db.Episodes
                .Where(e => e.PodcastFeedId == podcastId && e.State == EpisodeState.Skipped)
                .OrderByDescending(e => e.PublishedAt)
                .Take(count)
                .ToListAsync(cancellationToken);
            if (episodes.Count == 0)
            {
                throw new CommandRefusedException("This podcast has no skipped episodes left to include.");
            }

            var now = time.GetUtcNow().UtcDateTime;
            foreach (var episode in episodes)
            {
                episode.State = episode.TranscriptLinks.Count > 0 ? EpisodeState.New : EpisodeState.AwaitingTranscription;
                episode.Attempts = 0;
                episode.LastError = null;
                episode.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await poller.PollAsync(podcastId, cancellationToken);
    }

    private const int MaxLookupKeyLength = BookMention.MaxTitleLength + BookMention.MaxAuthorLength;

    /// <summary>
    /// Stored, and shown in the UI, when a command fails for a reason other than a refusal. The exception's own message
    /// can carry a database path or a feed URL with its token, so it goes only to the log.
    /// </summary>
    public const string FailedError = "The Worker couldn't do this. Its log has the details.";

    /// <summary>
    /// Makes every mention with this lookup key due for matching again, so the matcher applies whatever review decision
    /// is now stored for it, or matches normally if it was undone. Only invalidates: the matcher alone changes match
    /// state, and mentions keep their current answer until it does.
    /// </summary>
    private async Task RematchKeyAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.BookMentions.Where(m => m.LookupKey == key).ExecuteUpdateAsync(s => s.SetProperty(m => m.MatcherVersion, (string?)null), cancellationToken);
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
            ?? throw new CommandRefusedException("That episode no longer exists.");
        if (episode.Transcript is null || episode.State is not (EpisodeState.Extracted or EpisodeState.ExtractionFailed))
        {
            throw new CommandRefusedException($"This episode can't be re-extracted while it's {episode.State}.");
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {Type} ({Id}) refused: {Reason}")]
    private partial void LogCommandRefused(WorkerCommandType type, Guid id, string reason);
}

/// <summary>
/// A command that can't apply to its target as it is now, e.g. prioritising an episode that's already finished. Not a
/// fault: the message is written for the person who sent it and is shown to them.
/// </summary>
public sealed class CommandRefusedException(string message) : Exception(message);
