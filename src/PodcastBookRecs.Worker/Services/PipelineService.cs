using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Pipeline;

namespace PodcastBookRecs.Worker.Services;

/// <summary>
/// Runs the Worker's loops side by side: UI commands, scheduled feed polls, transcript fetching, the heartbeat and,
/// when their providers are configured, transcription and book extraction.
/// A loop that throws logs the error and carries on after its delay, so one bad feed or a locked database
/// doesn't stop the others.
/// </summary>
internal sealed partial class PipelineService(
    IDbContextFactory<AppDbContext> dbFactory,
    CommandProcessor commands,
    FeedPoller poller,
    TranscriptFetcher transcripts,
    IOptions<PollingOptions> polling,
    TimeProvider time,
    ILogger<PipelineService> logger,
    EpisodeTranscriber? transcriber = null,
    EpisodeExtractor? extractor = null) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinPause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(10);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task>
        {
            Loop("commands", TimeSpan.FromSeconds(5), commands.ProcessPendingAsync, stoppingToken),
            Loop("feed polling", TimeSpan.FromMinutes(1), PollDueFeedsAsync, stoppingToken),
            Loop("transcript fetching", TimeSpan.FromSeconds(30), DrainTranscriptsAsync, stoppingToken),
            Loop("heartbeat", TimeSpan.FromSeconds(30), HeartbeatAsync, stoppingToken),
        };
        if (transcriber is not null)
        {
            loops.Add(BackendLoop("transcription", transcriber.CleanUpOrphansAsync, transcriber.ProcessNextAsync, stoppingToken));
        }

        if (extractor is not null)
        {
            loops.Add(BackendLoop("extraction", null, extractor.ProcessNextAsync, stoppingToken));
        }

        return Task.WhenAll(loops);
    }

    /// <summary>
    /// Processes episodes back to back while any are due, for a worker that depends on an external backend. When the
    /// backend is unreachable (or the disk is full), waits 30 seconds, doubling up to 10 minutes, without counting
    /// anything against the episode.
    /// </summary>
    private async Task BackendLoop(
        string name, Func<CancellationToken, Task>? startup, Func<CancellationToken, Task<WorkOutcome>> processNext, CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (startup is not null)
        {
            try
            {
                await startup(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogLoopFailed(ex, name + " start-up");
            }
        }

        var pause = MinPause;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var outcome = await processNext(stoppingToken);
                delay = outcome switch
                {
                    WorkOutcome.Worked => TimeSpan.Zero,
                    WorkOutcome.Idle => IdleDelay,
                    _ => pause,
                };
                pause = outcome == WorkOutcome.Paused ? TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, MaxPause.Ticks)) : MinPause;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogLoopFailed(ex, name);
                delay = IdleDelay;
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task Loop(string name, TimeSpan delay, Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await work(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogLoopFailed(ex, name);
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollDueFeedsAsync(CancellationToken cancellationToken)
    {
        var dueBefore = time.GetUtcNow().UtcDateTime.AddMinutes(-polling.Value.IntervalMinutes);
        List<Guid> due;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            due = await db.PodcastFeeds
                .Where(p => p.Enabled && (p.LastPolledAt == null || p.LastPolledAt <= dueBefore))
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var id in due)
        {
            await poller.PollAsync(id, cancellationToken);
        }
    }

    private async Task DrainTranscriptsAsync(CancellationToken cancellationToken)
    {
        while (await transcripts.ProcessNextAsync(cancellationToken))
        {
        }
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var status = await db.WorkerStatuses.FindAsync([WorkerStatusNames.Worker], cancellationToken);
        if (status is null)
        {
            status = new WorkerStatus { Name = WorkerStatusNames.Worker };
            db.WorkerStatuses.Add(status);
        }

        status.Healthy = true;
        status.UpdatedAt = time.GetUtcNow().UtcDateTime;
        status.Version = AppVersion.Display;
        status.Detail = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Loop} loop failed; retrying after its delay")]
    private partial void LogLoopFailed(Exception ex, string loop);
}
