using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Earmarked.Core.Data;

namespace Earmarked.Worker.Services;

/// <summary>
/// Writes each backend health check's result to <see cref="WorkerStatus"/>, where the UI's Status page reads it.
/// Runs on .NET's health-check publisher schedule, separately from the Worker heartbeat, so a powered-off backend
/// can't make the Worker itself look dead. Rows for backends that are no longer configured are removed.
/// </summary>
internal sealed class BackendStatusPublisher(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time) : IHealthCheckPublisher
{
    public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var rows = await db.WorkerStatuses.Where(s => s.Name != WorkerStatusNames.Worker).ToListAsync(cancellationToken);

        db.WorkerStatuses.RemoveRange(rows.Where(r => !report.Entries.ContainsKey(r.Name)));
        foreach (var (name, entry) in report.Entries)
        {
            var row = rows.FirstOrDefault(r => r.Name == name);
            if (row is null)
            {
                row = new WorkerStatus { Name = name };
                db.WorkerStatuses.Add(row);
            }

            row.Healthy = entry.Status == HealthStatus.Healthy;
            row.Detail = entry.Description;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
