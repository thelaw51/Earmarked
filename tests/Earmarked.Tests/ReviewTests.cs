using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Earmarked.Core.Backends;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Books;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Pipeline;
using Earmarked.Core.Transcripts;

namespace Earmarked.Tests;

/// <summary>The review backlog, the page's writes and the Worker's search runner, against a migrated SQLite file.</summary>
public sealed class ReviewTests : IAsyncLifetime
{
    private static readonly CatalogBook Almanac =
        new("OL1001W", "The Lighthouse Keeper's Almanac", null, ["Ines Calder"], 2019, 4, "https://openlibrary.org/works/OL1001W", null);

    private readonly string _root = Directory.CreateTempSubdirectory("earmarked-review-").FullName;
    private readonly ManualTime _time = new();
    private readonly FakeCatalog _catalog = new();
    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private CatalogSearchRunner _runner = null!;
    private List<(Guid Episode, Guid Transcript)> _episodes = [];
    private Guid _bookId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

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
            .Select(i => new PodcastFeed { FeedUrl = $"https://feeds.test/show-{i}.xml", Title = $"Show {i}", AddedAt = Now })
            .ToList();
        var episodes = Enumerable.Range(0, 3).Select(i => new Episode
        {
            PodcastFeed = feeds[i % 2],
            IdentityKey = $"ep-{i}",
            Title = $"Episode {i}",
            State = EpisodeState.Extracted,
            PublishedAt = Now.AddDays(i),
            Transcript = new Transcript
            {
                Source = TranscriptSource.Transcribed,
                Format = TranscriptFormat.SpeechToText,
                Segments = [new(TimeSpan.Zero, null, null, "Some words.")],
            },
        }).ToList();
        db.Episodes.AddRange(episodes);
        var book = new Book
        {
            Provider = "Test",
            CatalogId = "OL1001W",
            Title = "The Lighthouse Keeper's Almanac",
            Authors = "Ines Calder",
            InfoUrl = "https://openlibrary.org/works/OL1001W",
            CreatedAt = Now,
        };
        db.Books.Add(book);
        await db.SaveChangesAsync(Ct);
        _bookId = book.Id;
        _episodes = episodes.Select(e => (e.Id, e.Transcript!.Id)).ToList();
        _runner = new CatalogSearchRunner(_dbFactory, new BookCatalogChain([_catalog]), _time, NullLogger<CatalogSearchRunner>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Unfound_failed_and_doubtful_titles_are_in_the_backlog_and_sound_matches_arent()
    {
        await AddMentionAsync(0, "Small Rivers", null, MatchState.NoMatch);
        await AddMentionAsync(0, "Harbor Lights", null, MatchState.LookupFailed);
        await AddMentionAsync(1, "The Lighthouse Keeper's Almanac", "Calder", MatchState.Matched, doubts: MatchDoubts.FewEditions);
        await AddMentionAsync(1, "Quiet Water", "Ames", MatchState.Matched, confidence: 0.4);
        await AddMentionAsync(2, "Sound Book", "Okafor", MatchState.Matched);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var items = await ReviewBacklog.LoadAsync(db, Ct);

        Assert.Equal(4, await ReviewBacklog.CountAsync(db, Ct));
        Assert.Equal(
            new Dictionary<string, ReviewReasons>
            {
                ["Small Rivers"] = ReviewReasons.NotFound,
                ["Harbor Lights"] = ReviewReasons.LookupFailed,
                ["The Lighthouse Keeper's Almanac"] = ReviewReasons.FewEditions,
                ["Quiet Water"] = ReviewReasons.LowConfidence,
            },
            items.ToDictionary(i => i.Title, i => i.Reasons));
        Assert.Equal(_bookId, items.Single(i => i.Title == "Quiet Water").Book?.Id);
    }

    [Fact]
    public async Task One_confident_mention_keeps_a_title_out_of_the_backlog()
    {
        await AddMentionAsync(0, "Quiet Water", "Ames", MatchState.Matched, confidence: 0.3);
        await AddMentionAsync(1, "Quiet Water", "J. Ames", MatchState.Matched, confidence: 0.9);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Equal(0, await ReviewBacklog.CountAsync(db, Ct));
    }

    [Fact]
    public async Task An_item_counts_its_mentions_across_episodes_and_podcasts()
    {
        await AddMentionAsync(0, "Small Rivers", null, MatchState.NoMatch);
        await AddMentionAsync(1, "Small Rivers", null, MatchState.NoMatch);
        await AddMentionAsync(2, "Small Rivers", null, MatchState.NoMatch);
        await AddMentionAsync(2, "Small Rivers", null, MatchState.NoMatch);
        await AddMentionAsync(0, "Harbor Lights", null, MatchState.NoMatch);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var item = (await ReviewBacklog.LoadAsync(db, Ct))[0];

        Assert.Equal(("Small Rivers", 4, 3, 2, ReviewBacklog.MaxQuotes), (item.Title, item.Mentions, item.Episodes, item.Podcasts, item.Quotes.Count));
    }

    [Fact]
    public async Task Decided_titles_and_ones_waiting_to_be_matched_again_are_left_out()
    {
        await AddMentionAsync(0, "Small Rivers", null, MatchState.NoMatch);
        await AddMentionAsync(1, "Harbor Lights", null, MatchState.NoMatch, matcherVersion: "match-v2");

        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await ReviewActions.RejectAsync(db, Key("Small Rivers", null), ReviewVerdict.NotABook, null, Now, Ct);
        }

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Empty(await ReviewBacklog.LoadAsync(check, Ct));
        Assert.Equal(0, await ReviewBacklog.CountAsync(check, Ct));
    }

    [Fact]
    public async Task A_decision_is_saved_with_the_command_that_rematches_its_key()
    {
        await AddMentionAsync(0, "Small Rivers", null, MatchState.NoMatch);

        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await ReviewActions.RejectAsync(db, Key("Small Rivers", null), ReviewVerdict.NotABook, "  a  newspaper column ", Now, Ct);
        }

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        var decision = await check.ReviewDecisions.SingleAsync(Ct);
        var command = await check.WorkerCommands.SingleAsync(Ct);
        Assert.Equal((Key("Small Rivers", null), "Small Rivers", "a  newspaper column"), (decision.LookupKey, decision.Title, decision.Note));
        Assert.Equal((WorkerCommandType.ReviewDecided, decision.LookupKey), (command.Type, command.LookupKey));
    }

    [Fact]
    public async Task A_key_no_mention_has_is_refused()
    {
        await AddMentionAsync(0, "Small Rivers", null, MatchState.NoMatch);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);

        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.RejectAsync(db, "made up|key", ReviewVerdict.NotABook, null, Now, Ct));

        Assert.Empty(await db.ReviewDecisions.ToListAsync(Ct));
        Assert.Empty(await db.WorkerCommands.ToListAsync(Ct));
    }

    [Fact]
    public async Task Confirming_takes_the_book_from_the_mentions_and_needs_one()
    {
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", "Calder", MatchState.Matched, doubts: MatchDoubts.FewEditions);
        await AddMentionAsync(1, "Small Rivers", null, MatchState.NoMatch);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        await ReviewActions.ConfirmAsync(db, Key("The Lighthouse Keeper's Almanac", "Calder"), "  checked the cover ", Now, Ct);
        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.ConfirmAsync(db, Key("Small Rivers", null), null, Now, Ct));

        Assert.Equal(_bookId, (await db.ReviewDecisions.SingleAsync(Ct)).BookId);
    }

    [Fact]
    public async Task Deciding_again_replaces_the_decision_and_undoing_deletes_it()
    {
        await AddMentionAsync(0, "The Lighthouse Keeper's Almanac", "Calder", MatchState.Matched, doubts: MatchDoubts.FewEditions);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        await ReviewActions.RejectAsync(db, Key("The Lighthouse Keeper's Almanac", "Calder"), ReviewVerdict.WrongBook, "not this one", Now, Ct);
        _time.Advance(TimeSpan.FromMinutes(1));

        await ReviewActions.ConfirmAsync(db, Key("The Lighthouse Keeper's Almanac", "Calder"), "  checked the cover ", Now, Ct);
        var decision = await db.ReviewDecisions.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((ReviewVerdict.Confirmed, "checked the cover", Now), (decision.Verdict, decision.Note, decision.DecidedAt));

        await ReviewActions.UndoAsync(db, decision.Id, Now, Ct);
        Assert.Empty(await db.ReviewDecisions.ToListAsync(Ct));
        Assert.Equal(3, await db.WorkerCommands.CountAsync(c => c.LookupKey == decision.LookupKey, Ct));
        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.UndoAsync(db, decision.Id, Now, Ct));
    }

    [Fact]
    public async Task A_pick_is_copied_from_the_stored_search_never_from_the_page()
    {
        await AddMentionAsync(0, "Harbor Lights", null, MatchState.NoMatch);
        _catalog.Answer("Harbor Lights", Almanac);
        Guid searchId;
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            searchId = await ReviewActions.RequestSearchAsync(db, "Harbor Lights", null, Now, Ct);
        }

        await _runner.RunPendingAsync(Ct);

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.CorrectAsync(check, Key("Harbor Lights", null), searchId, "OL6666W", null, Now, Ct));
        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.CorrectAsync(check, Key("Harbor Lights", null), Guid.NewGuid(), "OL1001W", null, Now, Ct));
        await ReviewActions.CorrectAsync(check, Key("Harbor Lights", null), searchId, "OL1001W", null, Now, Ct);
        var decision = await check.ReviewDecisions.SingleAsync(Ct);
        Assert.Equal((ReviewVerdict.Corrected, Almanac.CatalogId, Almanac.InfoUrl), (decision.Verdict, decision.Pick?.CatalogId, decision.Pick?.InfoUrl));
    }

    [Fact]
    public async Task A_stored_result_without_https_links_cant_be_picked()
    {
        _catalog.Answer("Harbor Lights", Almanac with { InfoUrl = "http://openlibrary.org/works/OL1001W" });
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var searchId = await ReviewActions.RequestSearchAsync(db, "Harbor Lights", null, Now, Ct);
        await _runner.RunPendingAsync(Ct);

        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.CorrectAsync(db, Key("Harbor Lights", null), searchId, "OL1001W", null, Now, Ct));
    }

    [Fact]
    public async Task Keeping_as_is_stores_the_typed_text_only_in_the_pick_cleaned_and_capped()
    {
        await AddMentionAsync(0, "Harbor Lights", "Calder", MatchState.NoMatch);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);

        await ReviewActions.KeepAsIsAsync(db, Key("Harbor Lights", "Calder"), " Harbour\r\nLights" + new string('x', 400), "Ines\u0000Calder\u202E\u2028", "  self-published ", Now, Ct);

        var decision = await db.ReviewDecisions.SingleAsync(Ct);
        Assert.Equal((ReviewVerdict.KeptAsIs, "Harbor Lights", "Calder", "self-published", (Guid?)null),
            (decision.Verdict, decision.Title, decision.Author, decision.Note, decision.BookId));
        var pick = decision.Pick!;
        Assert.Equal((BookLookupProviders.Manual, decision.Id.ToString("N"), (string?)null), (pick.Provider, pick.CatalogId, pick.InfoUrl));
        Assert.Equal(BookMention.MaxTitleLength, pick.Title.Length);
        Assert.StartsWith("Harbour  Lights", pick.Title, StringComparison.Ordinal);
        Assert.Equal(["Ines Calder"], pick.Authors);
        Assert.True(CatalogMatcher.IsUsable(pick));
        Assert.Equal(WorkerCommandType.ReviewDecided, (await db.WorkerCommands.SingleAsync(Ct)).Type);
    }

    [Theory]
    [InlineData("Harbor Lights", "   ")]
    [InlineData("Harbor Lights", "\r\n\t")]
    [InlineData("Harbor Lights", "\u202E\u2029\u200B")]
    [InlineData("made up", "A Real Title")]
    public async Task Keeping_as_is_needs_a_title_and_a_stored_key(string mentioned, string typed)
    {
        await AddMentionAsync(0, "Harbor Lights", null, MatchState.NoMatch);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);

        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.KeepAsIsAsync(db, Key(mentioned, null), typed, null, null, Now, Ct));

        Assert.Empty(await db.ReviewDecisions.ToListAsync(Ct));
        Assert.Empty(await db.WorkerCommands.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_stored_result_claiming_to_be_manual_cant_be_picked()
    {
        await AddMentionAsync(0, "Harbor Lights", null, MatchState.NoMatch);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var id = Guid.NewGuid().ToString("N");
        var search = new CatalogSearch
        {
            Title = "Harbor Lights",
            CreatedAt = Now,
            CompletedAt = Now,
            Results = [new CatalogBook(id, "Harbour Lights", null, [], null, 0, null, null, BookLookupProviders.Manual)],
        };
        db.CatalogSearches.Add(search);
        await db.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.CorrectAsync(db, Key("Harbor Lights", null), search.Id, id, null, Now, Ct));
    }

    [Fact]
    public async Task The_runner_falls_back_to_the_title_alone_and_stores_the_results()
    {
        _catalog.Answer("The Lighthouse Keeper's Almanac", Almanac, onlyWithoutAuthor: true);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var id = await ReviewActions.RequestSearchAsync(db, "The Lighthouse Keeper's Almanac", "Somebody Else", Now, Ct);

        await _runner.RunPendingAsync(Ct);

        var search = await db.CatalogSearches.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
        Assert.Equal((Now, (string?)null, "OL1001W"), (search.CompletedAt, search.Error, Assert.Single(search.Results!).CatalogId));
        Assert.Equal(2, _catalog.Calls);
    }

    [Theory]
    [InlineData(true, CatalogSearchRunner.UnavailableError)]
    [InlineData(false, CatalogSearchRunner.UnreadableError)]
    public async Task A_failed_search_completes_with_our_own_message(bool unavailable, string expected)
    {
        _catalog.Throw = unavailable ? new BackendUnavailableException("secret detail") : new CatalogLookupFailedException("secret detail");
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        await ReviewActions.RequestSearchAsync(db, "Harbor Lights", null, Now, Ct);

        await _runner.RunPendingAsync(Ct);

        var search = await db.CatalogSearches.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((expected, (List<CatalogBook>?)null), (search.Error, search.Results));
    }

    [Fact]
    public async Task A_search_asks_every_catalogue_and_marks_each_result_with_the_one_that_answered()
    {
        var apple = new FakeCatalog(BookLookupProviders.AppleBooks);
        var runner = new CatalogSearchRunner(_dbFactory, new BookCatalogChain([_catalog, apple]), _time, NullLogger<CatalogSearchRunner>.Instance);
        _catalog.Answer("Harbor Lights", Almanac with { Provider = BookLookupProviders.AppleBooks });
        apple.Answer("Harbor Lights", Almanac with { CatalogId = "900000001", Provider = BookLookupProviders.Manual, EditionCount = null });
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var id = await ReviewActions.RequestSearchAsync(db, "Harbor Lights", null, Now, Ct);

        await runner.RunPendingAsync(Ct);

        var search = await db.CatalogSearches.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
        Assert.Null(search.Error);
        Assert.Equal([(BookLookupProviders.OpenLibrary, "OL1001W"), (BookLookupProviders.AppleBooks, "900000001")],
            search.Results!.Select(r => (r.Provider, r.CatalogId)));
    }

    [Fact]
    public async Task A_catalogue_that_takes_too_long_is_left_out_and_the_others_answers_are_kept()
    {
        var apple = new FakeCatalog(BookLookupProviders.AppleBooks) { Hang = true };
        var runner = new CatalogSearchRunner(_dbFactory, new BookCatalogChain([_catalog, apple]), _time, NullLogger<CatalogSearchRunner>.Instance)
        {
            SearchTimeout = TimeSpan.FromMilliseconds(200),
        };
        _catalog.Answer("Harbor Lights", Almanac);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var id = await ReviewActions.RequestSearchAsync(db, "Harbor Lights", null, Now, Ct);

        await runner.RunPendingAsync(Ct);

        var search = await db.CatalogSearches.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
        Assert.Equal((CatalogSearchRunner.PartialError, "OL1001W"), (search.Error, Assert.Single(search.Results!).CatalogId));
    }

    [Fact]
    public async Task A_search_fails_only_when_every_catalogue_fails()
    {
        var apple = new FakeCatalog(BookLookupProviders.AppleBooks) { Throw = new BackendUnavailableException("down") };
        var runner = new CatalogSearchRunner(_dbFactory, new BookCatalogChain([_catalog, apple]), _time, NullLogger<CatalogSearchRunner>.Instance);
        _catalog.Answer("Harbor Lights", Almanac);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var partly = await ReviewActions.RequestSearchAsync(db, "Harbor Lights", null, Now, Ct);
        await runner.RunPendingAsync(Ct);

        _catalog.Throw = new CatalogLookupFailedException("odd");
        var wholly = await ReviewActions.RequestSearchAsync(db, "Small Rivers", null, Now, Ct);
        await runner.RunPendingAsync(Ct);

        var searches = await db.CatalogSearches.AsNoTracking().ToDictionaryAsync(s => s.Id, Ct);
        Assert.Equal((CatalogSearchRunner.PartialError, 1), (searches[partly].Error, searches[partly].Results?.Count));
        Assert.Equal((CatalogSearchRunner.UnreadableError, (List<CatalogBook>?)null), (searches[wholly].Error, searches[wholly].Results));
    }

    [Fact]
    public async Task Old_searches_expire_unasked_and_are_deleted_after_a_day()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var stale = await ReviewActions.RequestSearchAsync(db, "Harbor Lights", null, Now, Ct);
        _time.Advance(_runner.SearchTimeout + TimeSpan.FromSeconds(1));

        await _runner.RunPendingAsync(Ct);
        Assert.Equal(CatalogSearchRunner.ExpiredError, (await db.CatalogSearches.AsNoTracking().SingleAsync(Ct)).Error);
        Assert.Equal(0, _catalog.Calls);

        _time.Advance(CatalogSearchRunner.KeepFor);
        await _runner.RunPendingAsync(Ct);
        Assert.False(await db.CatalogSearches.AnyAsync(s => s.Id == stale, Ct));
    }

    [Fact]
    public async Task An_idle_pass_writes_nothing_once_the_clean_up_has_run()
    {
        var writes = new WriteCounter();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "test.db")}")
            .AddInterceptors(new SqlitePragmaInterceptor(), writes)
            .Options;
        var runner = new CatalogSearchRunner(new PooledDbContextFactory<AppDbContext>(options), new BookCatalogChain([_catalog]), _time, NullLogger<CatalogSearchRunner>.Instance);
        await runner.RunPendingAsync(Ct);
        writes.Count = 0;

        _time.Advance(TimeSpan.FromMinutes(5));
        await runner.RunPendingAsync(Ct);

        Assert.Equal(0, writes.Count);
    }

    [Fact]
    public async Task Repeating_a_search_reuses_it_and_too_many_open_ones_are_refused()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var first = await ReviewActions.RequestSearchAsync(db, "Harbor Lights", null, Now, Ct);
        Assert.Equal(first, await ReviewActions.RequestSearchAsync(db, " Harbor Lights ", "", Now, Ct));

        for (var i = 1; i < ReviewActions.MaxOpenSearches; i++)
        {
            await ReviewActions.RequestSearchAsync(db, $"Title {i}", null, Now, Ct);
        }

        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.RequestSearchAsync(db, "One too many", null, Now, Ct));
        await Assert.ThrowsAsync<ReviewActionException>(() => ReviewActions.RequestSearchAsync(db, "   ", null, Now, Ct));
    }

    private static string Key(string title, string? author) => CatalogMatcher.LookupKey(title, author);

    private async Task AddMentionAsync(
        int episode, string title, string? author, MatchState state, double confidence = 0.9, MatchDoubts doubts = MatchDoubts.None,
        string matcherVersion = CatalogMatcher.Version)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        db.BookMentions.Add(new BookMention
        {
            EpisodeId = _episodes[episode].Episode,
            TranscriptId = _episodes[episode].Transcript,
            Title = title,
            Author = author,
            Quote = "Some words.",
            Kind = MentionKind.Discussed,
            Confidence = confidence,
            Provider = "Test",
            Model = "test",
            PromptVersion = "test",
            CreatedAt = Now,
            MatchState = state,
            BookId = state == MatchState.Matched ? _bookId : null,
            Doubts = doubts,
            LookupKey = CatalogMatcher.LookupKey(title, author),
            MatcherVersion = matcherVersion,
        });
        await db.SaveChangesAsync(Ct);
        _time.Advance(TimeSpan.FromMilliseconds(10));
    }

    private sealed class FakeCatalog(string provider = BookLookupProviders.OpenLibrary) : IBookCatalog
    {
        private readonly Dictionary<string, (CatalogBook Book, bool OnlyWithoutAuthor)> _answers = new(StringComparer.OrdinalIgnoreCase);

        public string Provider => provider;
        public int Calls { get; private set; }
        public Exception? Throw { get; set; }

        /// <summary>Never answers: the search waits until it's cancelled.</summary>
        public bool Hang { get; set; }

        public void Answer(string title, CatalogBook book, bool onlyWithoutAuthor = false) => _answers[title] = (book, onlyWithoutAuthor);

        public Task<BookDetails?> GetDetailsAsync(string catalogId, CancellationToken cancellationToken) => Task.FromResult<BookDetails?>(null);

        public async Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken)
        {
            Calls++;
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return _answers.TryGetValue(title, out var a) && (author is null || !a.OnlyWithoutAuthor) ? [a.Book] : [];
        }
    }

    /// <summary>Counts statements that write, so a test can check a pass only read.</summary>
    private sealed class WriteCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Count++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
