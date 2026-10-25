using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Pipeline;

namespace PodcastBookRecs.Worker.Services;

/// <summary>
/// Runs the Worker's loops side by side: UI commands, scheduled feed polls, transcript fetching, the heartbeat and,
/// when a provider is configured, transcription.
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
    EpisodeTranscriber? transcriber = null) : BackgroundService
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
            loops.Add(TranscriptionLoop(transcriber, stoppingToken));
        }

        return Task.WhenAll(loops);
    }

    /// <summary>
    /// Transcribes episodes back to back while any are due. When the backend is unreachable or the disk is full,
    /// waits 30 seconds, doubling up to 10 minutes, without counting anything against the episode.
    /// </summary>
    private async Task TranscriptionLoop(EpisodeTranscriber worker, CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await worker.CleanUpOrphansAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogLoopFailed(ex, "transcription clean-up");
        }

        var pause = MinPause;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var outcome = await worker.ProcessNextAsync(stoppingToken);
                delay = outcome switch
                {
                    TranscriberOutcome.Worked => TimeSpan.Zero,
                    TranscriberOutcome.Idle => IdleDelay,
                    _ => pause,
                };
                pause = outcome == TranscriberOutcome.Paused ? TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, MaxPause.Ticks)) : MinPause;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogLoopFailed(ex, "transcription");
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
