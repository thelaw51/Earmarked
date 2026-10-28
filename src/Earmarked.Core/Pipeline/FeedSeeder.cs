using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Earmarked.Core.Data;
using Earmarked.Core.Feeds;

namespace Earmarked.Core.Pipeline;

/// <summary>Imports feeds listed in config.json. The database stays the source of truth: removing a feed from config does nothing.</summary>
public sealed partial class FeedSeeder(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<FeedSeeder> logger)
{
    public async Task SeedAsync(IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = (await db.PodcastFeeds.Select(p => p.FeedUrl).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        foreach (var url in urls.Select(u => u.Trim()).Distinct(StringComparer.Ordinal))
        {
            var redacted = FeedUrlRedactor.Redact(url);
            if (!FeedUrl.IsValid(url))
            {
                LogInvalid(redacted);
                continue;
            }

            if (existing.Add(url))
            {
                db.PodcastFeeds.Add(new PodcastFeed { FeedUrl = url, AddedAt = time.GetUtcNow().UtcDateTime });
                LogAdded(redacted);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported feed {Feed} from config")]
    private partial void LogAdded(string feed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring feed {Feed} in config: not an http(s) URL")]
    private partial void LogInvalid(string feed);
}
