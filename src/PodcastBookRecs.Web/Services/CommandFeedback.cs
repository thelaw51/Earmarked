using Microsoft.EntityFrameworkCore;
using MudBlazor;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Web.Services;

/// <summary>
/// Sends commands to the Worker and tells the person when the Worker refuses one, which would otherwise only reach the
/// log. One per circuit: watching stops when the circuit closes.
/// </summary>
public sealed class CommandFeedback(IDbContextFactory<AppDbContext> dbFactory, ISnackbar snackbar, TimeProvider time) : IDisposable
{
    /// <summary>
    /// The Worker reads commands every 5 seconds, so most answer well inside this. A command still running after it
    /// (polling every feed, say) finishes without a message.
    /// </summary>
    public static readonly TimeSpan WatchFor = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);

    private readonly CancellationTokenSource _closing = new();

    /// <summary>
    /// Saves the command, says it was sent, and keeps watching it in the background. <paramref name="queued"/> should
    /// say the command was sent, not that it's done. False if it couldn't be saved.
    /// </summary>
    public async Task<bool> QueueAsync(WorkerCommand command, string queued)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            command.CreatedAt = time.GetUtcNow().UtcDateTime;
            db.WorkerCommands.Add(command);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            snackbar.Add($"Couldn't send that to the Worker: {ex.Message}", Severity.Error);
            return false;
        }

        snackbar.Add(queued, Severity.Info);
        Watch(command.Id);
        return true;
    }

    /// <summary>Watches a command saved elsewhere, e.g. with a review decision, and reports a refusal.</summary>
    public void Watch(Guid commandId) => _ = WatchAsync(commandId);

    private async Task WatchAsync(Guid commandId)
    {
        var until = time.GetUtcNow() + WatchFor;
        try
        {
            using var timer = new PeriodicTimer(PollEvery, time);
            while (await timer.WaitForNextTickAsync(_closing.Token) && time.GetUtcNow() < until)
            {
                await using var db = await dbFactory.CreateDbContextAsync(_closing.Token);
                var result = await db.WorkerCommands.AsNoTracking()
                    .Where(c => c.Id == commandId)
                    .Select(c => new { c.CompletedAt, c.Error })
                    .FirstOrDefaultAsync(_closing.Token);
                if (result?.CompletedAt is not null)
                {
                    // The Worker stores only its own refusal messages or a fixed failure message, never exception text.
                    if (result.Error is not null)
                    {
                        snackbar.Add($"The Worker didn't do that: {result.Error}", Severity.Warning);
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Watching is a courtesy: a database hiccup here mustn't surface as an unhandled error in the circuit.
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}
