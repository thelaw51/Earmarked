using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Pipeline;

namespace PodcastBookRecs.Worker.Services;

/// <summary>
/// Runs the Worker's loops side by side: UI commands, scheduled feed polls, transcript fetching and the heartbeat.
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
    ILogger<PipelineService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            Loop("commands", TimeSpan.FromSeconds(5), commands.ProcessPendingAsync, stoppingToken),
            Loop("feed polling", TimeSpan.FromMinutes(1), PollDueFeedsAsync, stoppingToken),
            Loop("transcript fetching", TimeSpan.FromSeconds(30), DrainTranscriptsAsync, stoppingToken),
            Loop("heartbeat", TimeSpan.FromSeconds(30), HeartbeatAsync, stoppingToken));

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
