using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Pipeline;

namespace PodcastBookRecs.Worker.Services;

/// <summary>
/// Applies EF Core migrations and imports config feeds before anything else starts. The Worker is the only
/// process that migrates; the UI waits until no migrations are pending.
/// </summary>
internal sealed partial class DatabaseMigrator(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<PathsOptions> paths,
    IConfiguration configuration,
    FeedSeeder seeder,
    ILogger<DatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Value.Data);
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            await db.Database.MigrateAsync(cancellationToken);
            LogMigrated(pending.Count, paths.Value.DatabaseFile);
        }

        var feeds = configuration.GetSection("Feeds").Get<List<FeedSeed>>() ?? [];
        await seeder.SeedAsync(feeds.Select(f => f.Url), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Count} migrations to {Database}")]
    private partial void LogMigrated(int count, string database);
}
