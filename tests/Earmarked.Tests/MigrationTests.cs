using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Data;
using Earmarked.Core.Transcripts;

namespace Earmarked.Tests;

/// <summary>Migrations that rebuild a table, run over a database that already holds rows pointing into it.</summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"earmarked-migration-{Guid.NewGuid():N}.db");
    private DbContextOptions<AppDbContext> _options = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_path}")
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// SQLite can't drop NOT NULL in place, so EF copies Books into a new table and drops the old one. With foreign
    /// keys on, that drop would set every BookId pointing at it to null.
    /// </summary>
    [Fact]
    public async Task Allowing_books_without_links_keeps_every_link_to_a_book()
    {
        await using (var db = new AppDbContext(_options))
        {
            await db.Database.MigrateAsync(Ct);
            await db.GetService<IMigrator>().MigrateAsync("AddCatalogSearches", Ct);
            await SeedAsync(db);
        }

        await using (var db = new AppDbContext(_options))
        {
            await db.Database.MigrateAsync(Ct);
        }

        await using var check = new AppDbContext(_options);
        var book = await check.Books.SingleAsync(Ct);
        Assert.Equal((Guid?)book.Id, (await check.BookMentions.SingleAsync(Ct)).BookId);
        Assert.Equal((Guid?)book.Id, (await check.ReviewDecisions.SingleAsync(Ct)).BookId);
        check.Books.Add(new Book { Provider = book.Provider, CatalogId = book.CatalogId, Title = "Copy", CreatedAt = DateTime.UtcNow });
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync(Ct));
        Assert.Equal(2067, Assert.IsType<SqliteException>(duplicate.InnerException).SqliteExtendedErrorCode);
    }

    /// <summary>Rows written with SQL, because the model is ahead of the schema at this point.</summary>
    private static async Task SeedAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var (feed, episode, transcript, book, mention, decision) =
            (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var key = CatalogMatcher.LookupKey("Small Rivers", null);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO PodcastFeeds (Id, FeedUrl, Enabled, AddedAt) VALUES ({feed}, 'https://feeds.test/a.xml', 1, {now})", Ct);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Episodes (Id, PodcastFeedId, IdentityKey, Title, State, Attempts, TranscriptLinks, DiscoveredAt, UpdatedAt) VALUES ({episode}, {feed}, 'ep-1', 'Episode 1', {(int)EpisodeState.Extracted}, 0, '[]', {now}, {now})", Ct);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Transcripts (Id, EpisodeId, Source, Format, Segments, CreatedAt) VALUES ({transcript}, {episode}, {(int)TranscriptSource.Transcribed}, {(int)TranscriptFormat.SpeechToText}, '[]', {now})", Ct);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO Books (Id, Provider, CatalogId, Title, Authors, InfoUrl, CreatedAt) VALUES ({book}, 'OpenLibrary', 'OL1W', 'Small Rivers', '', 'https://catalog.test/OL1W', {now})", Ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO BookMentions (Id, EpisodeId, TranscriptId, Title, Quote, Kind, Confidence, Provider, Model, PromptVersion, CreatedAt, MatchState, BookId, Doubts, LookupKey)
            VALUES ({mention}, {episode}, {transcript}, 'Small Rivers', 'Some words.', 0, 0.9, 'Test', 'test', 'test', {now}, {(int)MatchState.Matched}, {book}, 0, {key})
            """, Ct);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO ReviewDecisions (Id, LookupKey, Title, Verdict, BookId, DecidedAt) VALUES ({decision}, {key}, 'Small Rivers', 0, {book}, {now})", Ct);
    }
}
