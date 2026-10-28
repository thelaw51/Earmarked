using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Earmarked.Core.Data;
using Earmarked.Web.Services;

namespace Earmarked.Tests;

/// <summary>How pages report failures: a fixed message on screen, the exception in the log.</summary>
public sealed class UiErrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_failed_load_is_logged_and_the_page_gets_a_fixed_message()
    {
        var logger = new RecordingLogger();

        var message = logger.LoadFailed(new InvalidOperationException("SQLite Error 1: no such table: /data/secret"), "this book");

        Assert.Equal("Couldn't load this book. The web app's log has the details.", message);
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Levels));
    }

    [Fact]
    public void A_cancelled_load_is_not_logged()
    {
        var logger = new RecordingLogger();

        logger.LoadFailed(new TaskCanceledException(), "status");

        Assert.Empty(logger.Levels);
    }

    [Fact]
    public async Task A_duplicate_feed_fails_with_the_unique_constraint_code_the_podcasts_page_catches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"earmarked-test-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            await using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync(Ct);
                db.PodcastFeeds.Add(new PodcastFeed { FeedUrl = "https://feeds.example.test/a.xml", AddedAt = DateTime.UtcNow });
                await db.SaveChangesAsync(Ct);
            }

            await using var second = new AppDbContext(options);
            second.PodcastFeeds.Add(new PodcastFeed { FeedUrl = "https://feeds.example.test/a.xml", AddedAt = DateTime.UtcNow });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(Ct));

            Assert.Equal(2067, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
