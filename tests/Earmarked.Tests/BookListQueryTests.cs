using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Earmarked.Core.Books;
using Earmarked.Core.Data;
using Earmarked.Core.Transcripts;

namespace Earmarked.Tests;

/// <summary>
/// The Books page's filters, against a migrated SQLite file. Three books: Émile is mentioned on both shows (days 0 and
/// 1), Quiet Water is recommended on show 1 (day 2), and Small Rivers comes up once on show 1 (day 0).
/// </summary>
public sealed class BookListQueryTests : IAsyncLifetime
{
    private static readonly DateTime Day0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Directory.CreateTempSubdirectory("earmarked-books-").FullName;
    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private List<Guid> _feeds = [];

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

        var feeds = Enumerable.Range(1, 2)
            .Select(i => new PodcastFeed { FeedUrl = $"https://feeds.test/show-{i}.xml", Title = $"Show {i}", AddedAt = Day0 })
            .ToList();
        var episodes = new[] { feeds[0], feeds[1], feeds[0] }.Select((feed, i) => new Episode
        {
            PodcastFeed = feed,
            IdentityKey = $"ep-{i}",
            Title = $"Episode {i}",
            State = EpisodeState.Extracted,
            PublishedAt = Day0.AddDays(i),
            Transcript = new Transcript
            {
                Source = TranscriptSource.Transcribed,
                Format = TranscriptFormat.SpeechToText,
                Segments = [new(TimeSpan.Zero, null, null, "Some words.")],
            },
        }).ToList();
        var emile = NewBook("OL1W", "Émile, or On Education", "Jean-Jacques Rousseau");
        var quiet = NewBook("OL2W", "Quiet Water", "J. Ames");
        var rivers = NewBook("OL3W", "Small Rivers", "");
        db.Episodes.AddRange(episodes);
        db.Books.AddRange(emile, quiet, rivers);
        db.BookMentions.AddRange(
            Mention(episodes[0], emile, MentionKind.Discussed),
            Mention(episodes[1], emile, MentionKind.Passing),
            Mention(episodes[2], quiet, MentionKind.Recommended),
            Mention(episodes[0], rivers, MentionKind.Passing));
        await db.SaveChangesAsync(Ct);
        _feeds = feeds.Select(f => f.Id).ToList();
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task With_no_filter_every_book_is_ranked()
    {
        var rows = await LoadAsync(BookFilter.None);

        Assert.Equal([(1, "Émile, or On Education"), (2, "Quiet Water"), (3, "Small Rivers")], rows.Select(r => (r.Rank, r.Book.Title)));
    }

    [Fact]
    public async Task A_podcast_filter_counts_only_that_podcasts_mentions()
    {
        var rows = await LoadAsync(new BookFilter([_feeds[1]]));

        var row = Assert.Single(rows);
        Assert.Equal(("Émile, or On Education", 1, 1, 1), (row.Book.Title, row.Rank, row.Ranking.Podcasts, row.Ranking.Episodes));
    }

    [Fact]
    public async Task A_date_range_includes_both_ends_and_reranks_the_window()
    {
        var rows = await LoadAsync(new BookFilter([], From: Day0.AddDays(1), To: Day0.AddDays(2)));

        // Inside days 1 to 2 each book has one podcast and one episode, so the more recent mention ranks first.
        Assert.Equal([(1, "Quiet Water", 1), (2, "Émile, or On Education", 1)], rows.Select(r => (r.Rank, r.Book.Title, r.Ranking.Podcasts)));
    }

    [Fact]
    public async Task Recommended_only_keeps_each_books_rank()
    {
        var rows = await LoadAsync(new BookFilter([], RecommendedOnly: true));

        var row = Assert.Single(rows);
        Assert.Equal((2, "Quiet Water"), (row.Rank, row.Book.Title));
    }

    [Theory]
    [InlineData("émile", "Émile, or On Education")]
    [InlineData("  AMES ", "Quiet Water")]
    public async Task Text_matches_title_or_author_ignoring_case(string text, string title)
    {
        var rows = await LoadAsync(new BookFilter([], Text: text));

        Assert.Equal(title, Assert.Single(rows).Book.Title);
    }

    [Fact]
    public async Task An_exports_mentions_are_the_ones_that_counted()
    {
        var filter = new BookFilter([_feeds[0]], Text: "émile");
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var rows = await BookListQuery.LoadAsync(db, filter, Ct);

        var mentions = await BookListQuery.LoadMentionsAsync(db, filter, rows, Ct);

        // Émile's mention on show 2 is outside the podcast filter, and Small Rivers' on show 1 isn't a listed book.
        var mention = Assert.Single(mentions);
        Assert.Equal(("Show 1", "Episode 0", MentionKind.Discussed), (mention.Podcast, mention.Episode, mention.Kind));
    }

    private async Task<IReadOnlyList<BookListRow>> LoadAsync(BookFilter filter)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await BookListQuery.LoadAsync(db, filter, Ct);
    }

    private static Book NewBook(string id, string title, string authors) => new()
    {
        Provider = "Test",
        CatalogId = id,
        Title = title,
        Authors = authors,
        InfoUrl = $"https://catalog.test/{id}",
        CreatedAt = Day0,
    };

    private static BookMention Mention(Episode episode, Book book, MentionKind kind) => new()
    {
        Episode = episode,
        Transcript = episode.Transcript!,
        Title = book.Title,
        Quote = "Some words.",
        Kind = kind,
        Confidence = 0.9,
        Provider = "Test",
        Model = "test",
        PromptVersion = "v1",
        MatchState = MatchState.Matched,
        Book = book,
        CreatedAt = Day0,
    };
}
