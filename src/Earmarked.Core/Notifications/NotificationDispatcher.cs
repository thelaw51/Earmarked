using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Earmarked.Core.Backends;
using Earmarked.Core.Data;

namespace Earmarked.Core.Notifications;

/// <summary>
/// Sends pending <see cref="Notification"/> rows, oldest first, on the Worker's notifications loop. While the server is
/// unreachable nothing counts against a message and it waits; a refused message is tried again with a back-off, then
/// dropped. Messages older than <see cref="MaxAge"/> are dropped unsent, so a long outage doesn't end in a flood of
/// stale notices, and finished rows are deleted after <see cref="KeepFor"/>.
/// </summary>
public sealed partial class NotificationDispatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationSender sender,
    TimeProvider time,
    ILogger<NotificationDispatcher> logger)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
    public const int MaxAttempts = 5;
    private const int BatchSize = 20;

    /// <summary>Only the notifications loop calls <see cref="SendPendingAsync"/>, so this needs no locking.</summary>
    private bool _unavailable;

    public async Task SendPendingAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var staleBefore = now - MaxAge;
        var dropped = await db.Notifications
            .Where(n => n.State == NotificationState.Pending && n.CreatedAt < staleBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.State, NotificationState.Dropped), cancellationToken);
        if (dropped > 0)
        {
            LogDroppedStale(dropped);
        }

        var forgetBefore = now - KeepFor;
        await db.Notifications.Where(n => n.State != NotificationState.Pending && n.CreatedAt < forgetBefore).ExecuteDeleteAsync(cancellationToken);

        var pending = await db.Notifications
            .Where(n => n.State == NotificationState.Pending && (n.NextAttemptAt == null || n.NextAttemptAt <= now))
            .OrderBy(n => n.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var notification in pending)
        {
            try
            {
                await sender.SendAsync(new NotificationMessage(notification.Kind, notification.Title, notification.Body, notification.ClickUrl), cancellationToken);
                notification.State = NotificationState.Sent;
                notification.SentAt = time.GetUtcNow().UtcDateTime;
                if (_unavailable)
                {
                    _unavailable = false;
                    LogResumed();
                }
            }
            catch (BackendUnavailableException ex)
            {
                if (!_unavailable)
                {
                    _unavailable = true;
                    LogUnavailable(ex.Message);
                }

                break;
            }
            catch (NotificationRejectedException ex)
            {
                notification.Attempts++;
                if (notification.Attempts >= MaxAttempts)
                {
                    notification.State = NotificationState.Dropped;
                    LogGaveUp(notification.Kind, notification.Attempts, ex.Message);
                }
                else
                {
                    notification.NextAttemptAt = now + TimeSpan.FromMinutes(Math.Pow(2, notification.Attempts));
                    LogRejected(notification.Kind, ex.Message);
                }
            }
            finally
            {
                // Saved one at a time, so a message that went out is never sent again after a crash mid-batch.
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notifications are waiting: {Reason}")]
    private partial void LogUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The notification server is answering again; sending the waiting notifications")]
    private partial void LogResumed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Kind} notification was refused and will be tried again: {Error}")]
    private partial void LogRejected(NotificationKind kind, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gave up on a {Kind} notification after {Attempts} refusals: {Error}")]
    private partial void LogGaveUp(NotificationKind kind, int attempts, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped {Count} notification(s) that waited more than a day to be sent")]
    private partial void LogDroppedStale(int count);
}
