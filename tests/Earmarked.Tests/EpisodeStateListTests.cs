using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Earmarked.Core.Data;
using Earmarked.Core.Pipeline;

namespace Earmarked.Tests;

/// <summary>
/// The Status page's per-state episode list, against a migrated SQLite file. Show 1 has a title; show 2 has none, so
/// its rows fall back to the feed's host.
/// </summary>
public sealed class EpisodeStateListTests : IAsyncLifetime
{
    private static readonly DateTime Day0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Directory.CreateTempSubdirectory("earmarked-states-").FullName;
    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private PodcastFeed _titled = null!;
    private PodcastFeed _untitled = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "test.db")}")
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        _dbFactory = new PooledDbContextFactory<AppDbContext>(options);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        await db.Database.MigrateAsync(Ct);
        _titled = new PodcastFeed { FeedUrl = "https://feeds.test/show-1.xml?token=secret", Title = "Show 1", AddedAt = Day0 };
        _untitled = new PodcastFeed { FeedUrl = "https://private.feeds.test/show-2.xml?token=secret", AddedAt = Day0 };
        db.PodcastFeeds.AddRange(_titled, _untitled);
        await db.SaveChangesAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Only_episodes_in_the_state_are_listed_with_their_podcast()
    {
        await AddAsync(
            Episode("transcribing", EpisodeState.Transcribing, _untitled, updated: 1),
            Episode("failed", EpisodeState.Failed, _titled, updated: 2, error: "Ran out of attempts"));

        var page = await LoadAsync(EpisodeState.Failed);

        var row = Assert.Single(page.Rows);
        Assert.Equal(("failed", "Show 1", _titled.Id, "Ran out of attempts", 1), (row.Title, row.Podcast, row.PodcastId, row.LastError, page.Total));
    }

    [Fact]
    public async Task A_podcast_without_a_title_shows_its_feed_host_only()
    {
        await AddAsync(Episode("transcribing", EpisodeState.Transcribing, _untitled, updated: 1));

        var row = Assert.Single((await LoadAsync(EpisodeState.Transcribing)).Rows);

        Assert.Equal("https://private.feeds.test/…", row.Podcast);
    }

    [Fact]
    public async Task Prioritised_episodes_come_first_then_the_most_recently_changed()
    {
        await AddAsync(
            Episode("old", EpisodeState.AwaitingTranscription, _titled, updated: 1),
            Episode("recent", EpisodeState.AwaitingTranscription, _titled, updated: 5),
            Episode("asked second", EpisodeState.AwaitingTranscription, _titled, updated: 2, prioritised: 4),
            Episode("asked first", EpisodeState.AwaitingTranscription, _titled, updated: 3, prioritised: 3));

        var page = await LoadAsync(EpisodeState.AwaitingTranscription);

        Assert.Equal(["asked first", "asked second", "recent", "old"], page.Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task The_limit_caps_rows_but_not_the_total()
    {
        await AddAsync(Enumerable.Range(0, 5).Select(i => Episode($"ep-{i}", EpisodeState.Skipped, _titled, updated: i)).ToArray());

        var page = await LoadAsync(EpisodeState.Skipped, limit: 2);

        Assert.Equal((2, 5), (page.Rows.Count, page.Total));
        Assert.Equal(["ep-4", "ep-3"], page.Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task With_no_state_everything_but_skipped_is_listed()
    {
        await AddAsync(
            Episode("skipped", EpisodeState.Skipped, _titled, updated: 3),
            Episode("extracted", EpisodeState.Extracted, _titled, updated: 2),
            Episode("extracting", EpisodeState.Extracting, _untitled, updated: 1));

        var page = await LoadAsync(null);

        Assert.Equal([("extracted", EpisodeState.Extracted), ("extracting", EpisodeState.Extracting)], page.Rows.Select(r => (r.Title, r.State)));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task Counts_cover_every_state_in_enum_order()
    {
        await AddAsync(
            Episode("a", EpisodeState.Failed, _titled, updated: 1),
            Episode("b", EpisodeState.New, _titled, updated: 2),
            Episode("c", EpisodeState.Failed, _untitled, updated: 3));

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var counts = await EpisodeStateList.CountAsync(db, Ct);

        Assert.Equal([(EpisodeState.New, 1), (EpisodeState.Failed, 2)], counts);
    }

    private static Episode Episode(string title, EpisodeState state, PodcastFeed feed, int updated, int? prioritised = null, string? error = null) => new()
    {
        PodcastFeedId = feed.Id,
        IdentityKey = title,
        Title = title,
        State = state,
        DiscoveredAt = Day0,
        UpdatedAt = Day0.AddHours(updated),
        PrioritisedAt = prioritised is { } p ? Day0.AddHours(p) : null,
        LastError = error,
    };

    private async Task AddAsync(params Episode[] episodes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        db.Episodes.AddRange(episodes);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<EpisodeStatePage> LoadAsync(EpisodeState? state, int limit = EpisodeStateList.MaxRows)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await EpisodeStateList.LoadAsync(db, state, limit, Ct);
    }
}
