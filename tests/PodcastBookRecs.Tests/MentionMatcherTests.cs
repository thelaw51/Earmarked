using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
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
    private CommandProcessor _commands = null!;
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
        _commands = new CommandProcessor(_dbFactory, null!, new EpisodeStateLock(),
            new TranscriptionWorkspace(Options.Create(new PathsOptions { Data = _root })), TimeProvider.System, NullLogger<CommandProcessor>.Instance);
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
    public async Task A_generic_reference_is_no_match_without_asking_the_catalogue()
    {
        _catalog.Answer("The Book", Almanac with { Title = "The Book" });
        await AddMentionAsync(0, "The Book", null);

        await RunUntilIdleAsync();

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(MatchState.NoMatch, (await db.BookMentions.SingleAsync(Ct)).MatchState);
        Assert.Equal(0, _catalog.Calls);
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

    [Fact]
    public async Task Not_a_book_takes_a_matched_title_off_the_book_list_without_asking_again()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        var first = await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        var second = await AddMentionAsync(1, "Lighthouse Keepers Almanac", null);
        await RunUntilIdleAsync();

        await DecideAsync("The Lighthouse Keeper's Almanac", null, ReviewVerdict.NotABook);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.All(await db.BookMentions.ToListAsync(Ct), m => Assert.Equal((MatchState.Rejected, (Guid?)null), (m.MatchState, m.BookId)));
        Assert.Equal(1, _catalog.Calls);
        Assert.Single(await db.Books.ToListAsync(Ct));
        Assert.Equal([first, second], (await db.BookMentions.OrderBy(m => m.CreatedAt).ToListAsync(Ct)).Select(m => m.Id));
    }

    [Fact]
    public async Task A_decision_survives_a_matcher_version_bump()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        await RunUntilIdleAsync();
        await DecideAsync("The Lighthouse Keeper's Almanac", null, ReviewVerdict.WrongBook);

        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.BookMentions.ExecuteUpdateAsync(s => s.SetProperty(m => m.MatcherVersion, "match-v0"), Ct);
        }

        await RunUntilIdleAsync();

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        var mention = await check.BookMentions.SingleAsync(Ct);
        Assert.Equal((MatchState.Rejected, CatalogMatcher.Version), (mention.MatchState, mention.MatcherVersion));
        Assert.Equal(1, _catalog.Calls);
    }

    [Fact]
    public async Task A_decision_applies_to_mentions_extracted_after_it()
    {
        await DecideAsync("The New Harbour Times", null, ReviewVerdict.NotABook);

        // Re-extraction deletes mentions and adds new ones with no lookup key, which the invalidation can't reach.
        await AddMentionAsync(1, "the new harbour times", null);
        await RunUntilIdleAsync();

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(MatchState.Rejected, (await db.BookMentions.SingleAsync(Ct)).MatchState);
        Assert.Equal(0, _catalog.Calls);
    }

    [Fact]
    public async Task Confirming_keeps_the_book_and_clears_its_doubts()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        await RunUntilIdleAsync();
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            Assert.Equal(MatchDoubts.NoAuthor, (await db.BookMentions.SingleAsync(Ct)).Doubts);
        }

        var bookId = await BookIdAsync();
        await DecideAsync("The Lighthouse Keeper's Almanac", null, ReviewVerdict.Confirmed, bookId: bookId);

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        var mention = await check.BookMentions.SingleAsync(Ct);
        Assert.Equal((MatchState.Matched, (Guid?)bookId, MatchDoubts.None), (mention.MatchState, mention.BookId, mention.Doubts));
    }

    [Fact]
    public async Task Correcting_adds_the_picked_book_once_and_points_every_mention_at_it()
    {
        var pick = new CatalogBook("OL2002W", "Harbour Lights", null, ["Ines Calder"], 2021, 3, "https://catalog.test/OL2002W", null);
        await AddMentionAsync(0, "Harbor Lights", "Calder");
        await RunUntilIdleAsync();

        await DecideAsync("Harbor Lights", "Calder", ReviewVerdict.Corrected, pick: pick);
        await AddMentionAsync(1, "Harbor Lights", "Ines Calder");
        await RunUntilIdleAsync();

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var book = await db.Books.SingleAsync(Ct);
        Assert.Equal(("OL2002W", "Harbour Lights"), (book.CatalogId, book.Title));
        Assert.All(await db.BookMentions.ToListAsync(Ct), m => Assert.Equal((MatchState.Matched, (Guid?)book.Id), (m.MatchState, m.BookId)));
        Assert.Equal(book.Id, (await db.ReviewDecisions.SingleAsync(Ct)).BookId);
    }

    [Theory]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("http://catalog.test/OL2002W", null)]
    [InlineData("https://catalog.test/OL2002W", "data:image/png;base64,AAAA")]
    public async Task A_pick_without_https_links_isnt_turned_into_a_book(string infoUrl, string? coverUrl)
    {
        var pick = new CatalogBook("OL2002W", "Harbour Lights", null, [], null, 1, infoUrl, coverUrl);
        await AddMentionAsync(0, "Harbor Lights", null);

        await DecideAsync("Harbor Lights", null, ReviewVerdict.Corrected, pick: pick);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(MatchState.LookupFailed, (await db.BookMentions.SingleAsync(Ct)).MatchState);
        Assert.Empty(await db.Books.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_confirmed_book_that_is_gone_doesnt_block_the_queue()
    {
        await AddMentionAsync(0, "Harbor Lights", null);
        await AddMentionAsync(1, "Small Rivers", null);
        await DecideAsync("Harbor Lights", null, ReviewVerdict.Confirmed, bookId: null);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var states = await db.BookMentions.OrderBy(m => m.CreatedAt).Select(m => m.MatchState).ToListAsync(Ct);
        Assert.Equal([MatchState.LookupFailed, MatchState.NoMatch], states);
    }

    [Fact]
    public async Task Undoing_a_decision_matches_the_title_normally_again()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", "Ines Calder");
        await RunUntilIdleAsync();
        await DecideAsync("The Lighthouse Keeper's Almanac", "Ines Calder", ReviewVerdict.NotABook);

        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.ReviewDecisions.ExecuteDeleteAsync(Ct);
        }

        await QueueReviewDecidedAsync(CatalogMatcher.LookupKey("The Lighthouse Keeper's Almanac", "Ines Calder"));
        await _commands.ProcessPendingAsync(Ct);
        await RunUntilIdleAsync();

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(MatchState.Matched, (await check.BookMentions.SingleAsync(Ct)).MatchState);
        Assert.Equal(2, _catalog.Calls);
    }

    [Fact]
    public async Task A_lookup_in_flight_when_a_decision_lands_doesnt_overwrite_it()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        _catalog.OnSearch = () => InsertDecisionAsync("The Lighthouse Keeper's Almanac", null, ReviewVerdict.NotABook, null, null).GetAwaiter().GetResult();

        await RunUntilIdleAsync();

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(MatchState.Rejected, (await db.BookMentions.SingleAsync(Ct)).MatchState);
        Assert.Equal(1, _catalog.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_review_command_without_a_key_is_refused_and_touches_no_mention(string? key)
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac);
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", null);
        await RunUntilIdleAsync();

        await QueueReviewDecidedAsync(key);
        await _commands.ProcessPendingAsync(Ct);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.NotNull((await db.WorkerCommands.SingleAsync(Ct)).Error);
        Assert.Equal(CatalogMatcher.Version, (await db.BookMentions.SingleAsync(Ct)).MatcherVersion);
    }

    /// <summary>Stores a decision and runs it through the command and the matcher, as the review page and the Worker would.</summary>
    private async Task DecideAsync(string title, string? author, ReviewVerdict verdict, Guid? bookId = null, CatalogBook? pick = null)
    {
        await InsertDecisionAsync(title, author, verdict, bookId, pick);
        await QueueReviewDecidedAsync(CatalogMatcher.LookupKey(title, author));
        await _commands.ProcessPendingAsync(Ct);
        await RunUntilIdleAsync();
    }

    private async Task InsertDecisionAsync(string title, string? author, ReviewVerdict verdict, Guid? bookId, CatalogBook? pick)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        db.ReviewDecisions.Add(new ReviewDecision
        {
            LookupKey = CatalogMatcher.LookupKey(title, author),
            Title = title,
            Author = author,
            Verdict = verdict,
            BookId = bookId,
            Pick = pick,
            DecidedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task QueueReviewDecidedAsync(string? key)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        db.WorkerCommands.Add(new WorkerCommand { Type = WorkerCommandType.ReviewDecided, LookupKey = key, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> BookIdAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return (await db.Books.SingleAsync(Ct)).Id;
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

        /// <summary>Runs during each search, after the matcher has decided to ask the catalogue.</summary>
        public Action? OnSearch { get; set; }

        public void Answer(string title, CatalogBook book, bool onlyWithoutAuthor = false) => _answers[title] = (book, onlyWithoutAuthor);

        public Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken)
        {
            Calls++;
            OnSearch?.Invoke();
            if (Throw is not null)
            {
                throw Throw;
            }

            IReadOnlyList<CatalogBook> found = _answers.TryGetValue(title, out var a) && (author is null || !a.OnlyWithoutAuthor) ? [a.Book] : [];
            return Task.FromResult(found);
        }
    }
}
