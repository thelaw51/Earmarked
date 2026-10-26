using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Pipeline;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Tests;

/// <summary>Runs the matcher against a migrated SQLite file with a fake catalogue.</summary>
public sealed class MentionMatcherTests : IAsyncLifetime
{
    private static readonly CatalogBook Almanac =
        new("OL1001W", "The Lighthouse Keeper's Almanac", null, ["Ines Calder"], 2019, 4, "https://catalog.test/OL1001W", null);

    private readonly string _root = Directory.CreateTempSubdirectory("pbr-matcher-").FullName;
    private readonly FakeCatalog _catalog = new();
    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private MentionMatcher _matcher = null!;
    private List<(Guid Episode, Guid Transcript)> _episodes = [];

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
        var feed = new PodcastFeed { FeedUrl = "https://feeds.test/show.xml", Title = "A Show", AddedAt = DateTime.UtcNow };
        var episodes = Enumerable.Range(1, 2).Select(i => new Episode
        {
            PodcastFeed = feed,
            IdentityKey = $"ep-{i}",
            Title = $"Episode {i}",
            State = EpisodeState.Extracted,
            Transcript = new Transcript
            {
                Source = TranscriptSource.Transcribed,
                Format = TranscriptFormat.SpeechToText,
                Segments = [new(TimeSpan.Zero, null, null, "Some words.")],
            },
        }).ToList();
        db.Episodes.AddRange(episodes);
        await db.SaveChangesAsync(Ct);
        _episodes = episodes.Select(e => (e.Id, e.Transcript!.Id)).ToList();
        _matcher = new MentionMatcher(_dbFactory, _catalog, TimeProvider.System, NullLogger<MentionMatcher>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Links_mentions_of_one_book_in_different_episodes_to_one_book_asking_once()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        var first = await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", "Ines Calder");
        var second = await AddMentionAsync(1, "Lighthouse Keepers Almanac", "I. Calder");

        await RunUntilIdleAsync();

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var book = await db.Books.SingleAsync(Ct);
        Assert.Equal(("OpenLibraryTest", "OL1001W", "Ines Calder"), (book.Provider, book.CatalogId, book.Authors));
        var mentions = await db.BookMentions.ToListAsync(Ct);
        Assert.All(mentions, m => Assert.Equal((MatchState.Matched, book.Id, CatalogMatcher.Version), (m.MatchState, m.BookId, m.MatcherVersion)));
        Assert.Equal(1, _catalog.Calls);
        Assert.Equal([first, second], mentions.OrderBy(m => m.CreatedAt).Select(m => m.Id));
    }

    [Fact]
    public async Task Keeps_a_title_the_catalogue_doesnt_have_as_no_match_and_doesnt_ask_again()
    {
        // Search returns something for a phrase that isn't a book, but not a book with that title.
        _catalog.Answer("The Harbour Case", Almanac);
        await AddMentionAsync(0, "The Harbour Case", null);
        await AddMentionAsync(1, "the harbour case", null);

        await RunUntilIdleAsync();

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.All(await db.BookMentions.ToListAsync(Ct), m => Assert.Equal((MatchState.NoMatch, (Guid?)null), (m.MatchState, m.BookId)));
        Assert.Empty(await db.Books.ToListAsync(Ct));
        Assert.Equal(1, _catalog.Calls);
    }

    [Fact]
    public async Task Searches_by_title_alone_when_the_author_search_finds_nothing()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac, onlyWithoutAuthor: true);
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", "Ines Calder");

        await RunUntilIdleAsync();

        Assert.Equal(2, _catalog.Calls);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(MatchState.Matched, (await db.BookMentions.SingleAsync(Ct)).MatchState);
    }

    [Fact]
    public async Task Pauses_without_touching_the_mention_when_the_catalogue_is_unavailable()
    {
        _catalog.Throw = new BackendUnavailableException("down");
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);

        Assert.Equal(WorkOutcome.Paused, await _matcher.ProcessNextAsync(Ct));

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var mention = await db.BookMentions.SingleAsync(Ct);
        Assert.Equal((MatchState.Pending, (string?)null), (mention.MatchState, mention.MatcherVersion));
    }

    [Fact]
    public async Task A_failed_lookup_is_recorded_and_the_queue_moves_on()
    {
        _catalog.Throw = new CatalogLookupFailedException("garbled");
        await AddMentionAsync(0, "Garbled", null);
        await AddMentionAsync(1, "Also Garbled", null);

        Assert.Equal(WorkOutcome.Worked, await _matcher.ProcessNextAsync(Ct));
        Assert.Equal(WorkOutcome.Worked, await _matcher.ProcessNextAsync(Ct));
        Assert.Equal(WorkOutcome.Idle, await _matcher.ProcessNextAsync(Ct));

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.All(await db.BookMentions.ToListAsync(Ct), m => Assert.Equal(MatchState.LookupFailed, m.MatchState));
    }

    [Fact]
    public async Task A_failed_lookup_is_tried_again_after_a_day()
    {
        _catalog.Throw = new CatalogLookupFailedException("garbled");
        var id = await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        Assert.Equal(WorkOutcome.Worked, await _matcher.ProcessNextAsync(Ct));
        Assert.Equal(WorkOutcome.Idle, await _matcher.ProcessNextAsync(Ct));

        _catalog.Throw = null;
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        var later = new MentionMatcher(_dbFactory, _catalog, new ShiftedTime(MentionMatcher.RetryFailedAfter + TimeSpan.FromMinutes(1)),
            NullLogger<MentionMatcher>.Instance);
        Assert.Equal(WorkOutcome.Worked, await later.ProcessNextAsync(Ct));

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(MatchState.Matched, (await db.BookMentions.SingleAsync(m => m.Id == id, Ct)).MatchState);
    }

    [Fact]
    public async Task Doesnt_copy_a_match_whose_book_is_gone()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        await RunUntilIdleAsync();
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.Books.ExecuteDeleteAsync(Ct);
        }

        var second = await AddMentionAsync(1, "The Lighthouse Keeper's Almanac", null);
        await RunUntilIdleAsync();

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.NotNull((await check.BookMentions.SingleAsync(m => m.Id == second, Ct)).BookId);
        Assert.Equal(2, _catalog.Calls);
    }

    [Fact]
    public async Task Mentions_matched_by_older_rules_are_matched_again()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        var id = await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.BookMentions.ExecuteUpdateAsync(s => s.SetProperty(m => m.MatchState, MatchState.NoMatch).SetProperty(m => m.MatcherVersion, "match-v0"), Ct);
        }

        await RunUntilIdleAsync();

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(MatchState.Matched, (await check.BookMentions.SingleAsync(m => m.Id == id, Ct)).MatchState);
    }

    [Fact]
    public async Task A_book_outlives_the_mentions_that_found_it()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        await RunUntilIdleAsync();

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        await db.Transcripts.Where(t => t.Id == _episodes[0].Transcript).ExecuteDeleteAsync(Ct);

        Assert.Empty(await db.BookMentions.ToListAsync(Ct));
        Assert.Single(await db.Books.ToListAsync(Ct));
    }

    private async Task RunUntilIdleAsync()
    {
        for (var i = 0; i < 20 && await _matcher.ProcessNextAsync(Ct) != WorkOutcome.Idle; i++)
        {
        }
    }

    private async Task<Guid> AddMentionAsync(int episode, string title, string? author)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var mention = new BookMention
        {
            EpisodeId = _episodes[episode].Episode,
            TranscriptId = _episodes[episode].Transcript,
            Title = title,
            Author = author,
            Quote = "Some words.",
            Kind = MentionKind.Discussed,
            Provider = "Test",
            Model = "test",
            PromptVersion = "test",
            CreatedAt = DateTime.UtcNow,
        };
        db.BookMentions.Add(mention);
        await db.SaveChangesAsync(Ct);
        await Task.Delay(5, Ct);
        return mention.Id;
    }

    private sealed class ShiftedTime(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }

    private sealed class FakeCatalog : IBookCatalog
    {
        private readonly Dictionary<string, (CatalogBook Book, bool OnlyWithoutAuthor)> _answers = new(StringComparer.OrdinalIgnoreCase);

        public string Provider => "OpenLibraryTest";
        public int Calls { get; private set; }
        public Exception? Throw { get; set; }

        public void Answer(string title, CatalogBook book, bool onlyWithoutAuthor = false) => _answers[title] = (book, onlyWithoutAuthor);

        public Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw is not null)
            {
                throw Throw;
            }

            IReadOnlyList<CatalogBook> found = _answers.TryGetValue(title, out var a) && (author is null || !a.OnlyWithoutAuthor) ? [a.Book] : [];
            return Task.FromResult(found);
        }
    }
}
