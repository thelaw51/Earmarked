using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Earmarked.Core.Backends;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Extraction;

namespace Earmarked.Core.Pipeline;

/// <summary>
/// Finds the books mentioned in one episode at a time: split the stored transcript into overlapping windows, ask the
/// extractor about each, keep only mentions whose quote is really in the transcript, merge repeats, and store them.
/// An episode takes a few minutes, so there is no checkpointing: a restart extracts the episode again.
/// </summary>
public sealed partial class EpisodeExtractor(
    IDbContextFactory<AppDbContext> dbFactory,
    IBookExtractor extractor,
    EpisodeStateLock stateLock,
    IOptions<ExtractionOptions> options,
    IOptions<TranscriptionOptions> transcription,
    IOptions<PollingOptions> polling,
    TimeProvider time,
    ILogger<EpisodeExtractor> logger)
{
    /// <summary>States an episode is in once it has a transcript and is ready for extraction.</summary>
    public static readonly EpisodeState[] ReadyStates = [EpisodeState.TranscriptFetched, EpisodeState.Transcribed];

    /// <summary>Only the extraction loop calls <see cref="ClaimNextAsync"/>, so this needs no locking.</summary>
    private bool _waitingForTranscription;

    public async Task<WorkOutcome> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var episode = await ClaimNextAsync(cancellationToken);
        if (episode is null)
        {
            return WorkOutcome.Idle;
        }

        try
        {
            await ExtractAsync(episode, cancellationToken);
            return WorkOutcome.Worked;
        }
        catch (BackendUnavailableException ex)
        {
            LogPaused(ex.Message);
            return WorkOutcome.Paused;
        }
        catch (ExtractionFailedException ex)
        {
            await RecordFailureAsync(episode.Id, episode.Title, ex.Message, cancellationToken);
            return WorkOutcome.Worked;
        }
    }

    /// <summary>
    /// Picks an episode already being extracted first (to finish it after a restart), then prioritised ones in the
    /// order they were asked for, then the newest one with a transcript. Claiming is a conditional update under the state lock.
    /// With <see cref="ExtractionOptions.WaitForTranscription"/>, a new episode that isn't prioritised waits while
    /// <see cref="TranscriptionWaitingAsync"/> says transcription has work.
    /// </summary>
    internal async Task<Episode?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        using var _ = await stateLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var candidate = await db.Episodes
            .Where(e => e.Transcript != null && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
            .Where(e => e.State == EpisodeState.Extracting || ReadyStates.Contains(e.State))
            .OrderBy(e => e.State == EpisodeState.Extracting ? 0 : 1)
            .ThenBy(e => e.PrioritisedAt == null)
            .ThenBy(e => e.PrioritisedAt)
            .ThenByDescending(e => e.PublishedAt)
            .Select(e => new { e.Id, e.State, e.PrioritisedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        if (candidate.State != EpisodeState.Extracting && candidate.PrioritisedAt is null && options.Value.WaitForTranscription && transcription.Value.Enabled)
        {
            var waiting = await TranscriptionWaitingAsync(db, now, cancellationToken);
            if (waiting != _waitingForTranscription)
            {
                _waitingForTranscription = waiting;
                if (waiting)
                {
                    LogWaiting();
                }
                else
                {
                    LogResumed();
                }
            }

            if (waiting)
            {
                return null;
            }
        }

        if (candidate.State != EpisodeState.Extracting)
        {
            var claimed = await db.Episodes
                .Where(e => e.Id == candidate.Id && e.State == candidate.State)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.State, EpisodeState.Extracting).SetProperty(e => e.UpdatedAt, now), cancellationToken);
            if (claimed != 1)
            {
                return null;
            }
        }

        return await db.Episodes.AsNoTracking().Include(e => e.PodcastFeed).Include(e => e.Transcript)
            .SingleAsync(e => e.Id == candidate.Id, cancellationToken);
    }

    /// <summary>
    /// Whether transcription has an episode due by its own rule while its Status row is healthy and current. An
    /// unhealthy or missing row means transcription can't use the GPU anyway, so extraction doesn't wait for it.
    /// </summary>
    private async Task<bool> TranscriptionWaitingAsync(AppDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var freshAfter = now - WorkerStatusNames.StaleAfter;
        var healthy = await db.WorkerStatuses.AnyAsync(
            s => s.Name == BackendHealth.Transcription && s.Healthy && s.UpdatedAt >= freshAfter, cancellationToken);
        return healthy && await db.Episodes.AnyAsync(EpisodeTranscriber.IsDue(now, transcription.Value), cancellationToken);
    }

    private async Task ExtractAsync(Episode episode, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var transcript = episode.Transcript!;
        var words = new TranscriptWords(transcript.Segments);
        var podcast = episode.PodcastFeed.Title ?? "Unknown podcast";

        var located = new List<LocatedMention>();
        var rejected = 0;
        foreach (var window in TranscriptWindows.Split(words, o.WindowWords, o.OverlapWords))
        {
            var found = await extractor.ExtractAsync(new ExtractionInput(podcast, episode.Title, window.Text), cancellationToken);
            foreach (var mention in found)
            {
                if (QuoteLocator.Find(words, mention.Quote, window.StartWord, window.EndWord) is { } match)
                {
                    located.Add(new LocatedMention(mention, match));
                }
                else
                {
                    rejected++;
                }
            }
        }

        var merged = MentionMerger.Merge(located);
        using (await stateLock.AcquireAsync(cancellationToken))
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var tracked = await db.Episodes.Include(e => e.Transcript).SingleOrDefaultAsync(e => e.Id == episode.Id, cancellationToken);
            if (tracked is null || tracked.State != EpisodeState.Extracting || tracked.Transcript?.Id != transcript.Id)
            {
                // Reset, re-run or deleted while we worked: these results are for a transcript that's gone or stale.
                return;
            }

            var now = time.GetUtcNow().UtcDateTime;
            await db.BookMentions.Where(m => m.EpisodeId == episode.Id).ExecuteDeleteAsync(cancellationToken);
            db.BookMentions.AddRange(merged.Select(m => new BookMention
            {
                EpisodeId = episode.Id,
                TranscriptId = transcript.Id,
                Title = m.Mention.Title,
                Author = m.Mention.Author,
                Quote = Cap(words.Span(m.Match.StartWord, m.Match.EndWord), BookMention.MaxQuoteLength),
                Kind = m.Mention.Kind,
                Confidence = m.Mention.Confidence,
                StartTime = words.StartOf(m.Match.StartWord),
                Provider = extractor.Provider,
                Model = extractor.Model,
                PromptVersion = extractor.PromptVersion,
                CreatedAt = now,
            }));
            tracked.State = EpisodeState.Extracted;
            tracked.PrioritisedAt = null;
            tracked.Attempts = 0;
            tracked.NextAttemptAt = null;
            tracked.LastError = null;
            tracked.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        LogExtracted(episode.Title, merged.Count, rejected);
    }

    /// <summary>Counts a failed attempt: back off, or give up after the last attempt. The transcript is kept either way.</summary>
    private async Task RecordFailureAsync(Guid episodeId, string title, string error, CancellationToken cancellationToken)
    {
        using var _ = await stateLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tracked = await db.Episodes.SingleOrDefaultAsync(e => e.Id == episodeId, cancellationToken);
        if (tracked is null || tracked.State != EpisodeState.Extracting)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        tracked.Attempts++;
        tracked.UpdatedAt = now;
        error = Cap(error, 500);
        if (tracked.Attempts >= polling.Value.MaxAttempts)
        {
            tracked.State = EpisodeState.ExtractionFailed;
            tracked.NextAttemptAt = null;
            tracked.PrioritisedAt = null;
            tracked.LastError = $"Extraction gave up after {tracked.Attempts} attempts: {error}";
        }
        else
        {
            tracked.NextAttemptAt = now + TranscriptFetcher.Backoff(tracked.Attempts);
            tracked.LastError = error;
        }

        await db.SaveChangesAsync(cancellationToken);
        LogFailed(title, tracked.Attempts, error);
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted {Episode}: {Mentions} book mention(s), {Rejected} rejected because their quote isn't in the transcript")]
    private partial void LogExtracted(string episode, int mentions, int rejected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extraction failed for {Episode} (attempt {Attempt}): {Error}")]
    private partial void LogFailed(string episode, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Extraction is waiting for transcription to finish (Extraction:WaitForTranscription)")]
    private partial void LogWaiting();

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcription has nothing waiting; extraction carries on")]
    private partial void LogResumed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extraction paused: {Reason}")]
    private partial void LogPaused(string reason);
}
