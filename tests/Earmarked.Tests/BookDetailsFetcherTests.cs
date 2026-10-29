using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Earmarked.Core.Backends;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Pipeline;

namespace Earmarked.Tests;

/// <summary>Runs the book details loop against a migrated SQLite file with a fake catalogue.</summary>
public sealed class BookDetailsFetcherTests : IAsyncLifetime
{
    private static readonly BookDetails Almanac = new("A year of weather notes.", ["Weather", "Islands"], 288, 4.2, 57);

    private readonly string _root = Directory.CreateTempSubdirectory("earmarked-details-").FullName;
    private readonly FakeCatalog _catalog = new();
    private readonly ShiftedTime _time = new();
    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private BookDetailsFetcher _fetcher = null!;

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
        _fetcher = new BookDetailsFetcher(_dbFactory, new BookCatalogChain([_catalog]), _time, NullLogger<BookDetailsFetcher>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Stores_the_catalogues_details_once_per_book()
    {
        _catalog.Details["OL1001W"] = Almanac;
        var id = await AddBookAsync("OL1001W");

        Assert.Equal(WorkOutcome.Worked, await _fetcher.ProcessNextAsync(Ct));
        Assert.Equal(WorkOutcome.Idle, await _fetcher.ProcessNextAsync(Ct));

        var book = await LoadAsync(id);
        Assert.Equal("A year of weather notes.", book.Description);
        Assert.Equal(["Weather", "Islands"], book.Subjects);
        Assert.Equal(288, book.PageCount);
        Assert.Equal(4.2, book.Rating);
        Assert.Equal(57, book.RatingCount);
        Assert.NotNull(book.DetailsCheckedAt);
        Assert.False(book.DetailsFailed);
        Assert.Equal(["OL1001W"], _catalog.Asked);
    }

    [Fact]
    public async Task Skips_manual_books_and_books_from_a_catalogue_no_longer_configured()
    {
        await AddBookAsync(Guid.CreateVersion7().ToString("N"), BookLookupProviders.Manual);
        await AddBookAsync("900000001", BookLookupProviders.AppleBooks);

        Assert.Equal(WorkOutcome.Idle, await _fetcher.ProcessNextAsync(Ct));
        Assert.Empty(_catalog.Asked);
    }

    [Fact]
    public async Task A_failed_ask_is_retried_after_a_day_behind_books_never_asked()
    {
        _catalog.Throw = new CatalogLookupFailedException("Open Library returned HTTP 404.");
        var failing = await AddBookAsync("OL1001W");

        Assert.Equal(WorkOutcome.Worked, await _fetcher.ProcessNextAsync(Ct));
        Assert.True((await LoadAsync(failing)).DetailsFailed);
        Assert.Equal(WorkOutcome.Idle, await _fetcher.ProcessNextAsync(Ct));

        _catalog.Throw = null;
        _catalog.Details["OL1001W"] = Almanac;
        var fresh = await AddBookAsync("OL1002W");
        _time.Advance(BookDetailsFetcher.RetryFailedAfter);

        Assert.Equal(WorkOutcome.Worked, await _fetcher.ProcessNextAsync(Ct));
        Assert.Equal(WorkOutcome.Worked, await _fetcher.ProcessNextAsync(Ct));
        Assert.Equal(["OL1001W", "OL1002W", "OL1001W"], _catalog.Asked);
        var retried = await LoadAsync(failing);
        Assert.False(retried.DetailsFailed);
        Assert.Equal("A year of weather notes.", retried.Description);
        Assert.NotNull((await LoadAsync(fresh)).DetailsCheckedAt);
    }

    [Fact]
    public async Task An_unreachable_catalogue_pauses_without_marking_the_book()
    {
        _catalog.Throw = new BackendUnavailableException("Can't reach Open Library.");
        var id = await AddBookAsync("OL1001W");

        Assert.Equal(WorkOutcome.Paused, await _fetcher.ProcessNextAsync(Ct));

        Assert.Null((await LoadAsync(id)).DetailsCheckedAt);
    }

    [Fact]
    public async Task A_book_the_catalogue_no_longer_has_is_marked_checked_and_keeps_what_it_had()
    {
        var id = await AddBookAsync("OL1001W", description: "Kept from before.");

        Assert.Equal(WorkOutcome.Worked, await _fetcher.ProcessNextAsync(Ct));

        var book = await LoadAsync(id);
        Assert.Equal("Kept from before.", book.Description);
        Assert.NotNull(book.DetailsCheckedAt);
        Assert.Equal(WorkOutcome.Idle, await _fetcher.ProcessNextAsync(Ct));
    }

    private async Task<Guid> AddBookAsync(string catalogId, string provider = BookLookupProviders.OpenLibrary, string? description = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var book = new Book
        {
            Provider = provider,
            CatalogId = catalogId,
            Title = $"Book {catalogId}",
            Description = description,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        };
        db.Books.Add(book);
        await db.SaveChangesAsync(Ct);
        _time.Advance(TimeSpan.FromMilliseconds(10));
        return book.Id;
    }

    private async Task<Book> LoadAsync(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await db.Books.AsNoTracking().SingleAsync(b => b.Id == id, Ct);
    }

    private sealed class ShiftedTime : TimeProvider
    {
        private TimeSpan _offset;

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + _offset;

        public void Advance(TimeSpan by) => _offset += by;
    }

    private sealed class FakeCatalog : IBookCatalog
    {
        public string Provider => BookLookupProviders.OpenLibrary;
        public Dictionary<string, BookDetails> Details { get; } = new(StringComparer.Ordinal);
        public List<string> Asked { get; } = [];
        public Exception? Throw { get; set; }

        public Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogBook>>([]);

        public Task<BookDetails?> GetDetailsAsync(string catalogId, CancellationToken cancellationToken)
        {
            Asked.Add(catalogId);
            return Throw is not null ? Task.FromException<BookDetails?>(Throw) : Task.FromResult(Details.GetValueOrDefault(catalogId));
        }
    }
}
