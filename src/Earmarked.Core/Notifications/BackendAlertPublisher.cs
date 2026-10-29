using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Earmarked.Core.Backends;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;

namespace Earmarked.Core.Notifications;

/// <summary>
/// Turns backend health reports into "problem with ..." and "... is back" notifications. Runs beside the Status page's
/// publisher on the same 30-second schedule and sees the same checks: the Mac's transcription server (including when
/// it answers but rejects all audio), the language model, and each book catalogue. A backend is reported once it has
/// been unhealthy for <see cref="NotificationsOptions.BackendAlertAfterMinutes"/>, and its recovery only if its outage
/// was reported. The notification server's own row is skipped: there would be nothing to deliver the alert.
/// State is kept in memory, so after a restart an ongoing outage is reported again once the wait has passed.
/// </summary>
public sealed class BackendAlertPublisher(
    IDbContextFactory<AppDbContext> dbFactory,
    NotificationOutbox outbox,
    IOptions<NotificationsOptions> options,
    TimeProvider time) : IHealthCheckPublisher
{
    private readonly Dictionary<string, Outage> _outages = [];

    public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var alertAfter = TimeSpan.FromMinutes(options.Value.BackendAlertAfterMinutes);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        foreach (var name in _outages.Keys.Where(n => !report.Entries.ContainsKey(n)).ToList())
        {
            _outages.Remove(name);
        }

        // Changes to what's remembered wait until the notices are saved, so a failed save sends them on the next report.
        var ended = new List<string>();
        var reported = new List<Outage>();
        foreach (var (name, entry) in report.Entries)
        {
            if (name == BackendHealth.Notifications)
            {
                continue;
            }

            if (entry.Status == HealthStatus.Healthy)
            {
                if (_outages.TryGetValue(name, out var over))
                {
                    if (over.Reported)
                    {
                        outbox.BackendRecovered(db, name, now - over.Since);
                    }

                    ended.Add(name);
                }

                continue;
            }

            if (!_outages.TryGetValue(name, out var outage))
            {
                outage = new Outage(now);
                _outages[name] = outage;
            }

            if (!outage.Reported && now - outage.Since >= alertAfter)
            {
                outbox.BackendDown(db, name, entry.Description);
                reported.Add(outage);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        ended.ForEach(name => _outages.Remove(name));
        reported.ForEach(outage => outage.Reported = true);
    }

    private sealed class Outage(DateTime since)
    {
        public DateTime Since { get; } = since;
        public bool Reported { get; set; }
    }
}
