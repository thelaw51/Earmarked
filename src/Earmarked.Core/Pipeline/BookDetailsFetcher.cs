using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Earmarked.Core.Backends;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Data;

namespace Earmarked.Core.Pipeline;

/// <summary>
/// Fills in each book's description, subjects, page count and rating from the catalogue it came from, one book at a
/// time. Books never asked come first, oldest first, so books found before details existed are covered too; a book
/// whose ask failed is tried again after <see cref="RetryFailedAfter"/>, behind them. Only books from a catalogue that
/// is still configured are due: a <c>Manual</c> book has no catalogue, and a book from one that was taken out of the
/// chain waits until it is back. The Worker spaces books <see cref="Gap"/> apart, because the adapter's pacing is shared
/// with matching and a backlog of hundreds of books mustn't hold up new mentions. Only this loop writes these columns,
/// and it never adds or removes a book.
/// </summary>
public sealed partial class BookDetailsFetcher(
    IDbContextFactory<AppDbContext> dbFactory,
    BookCatalogChain catalogs,
    TimeProvider time,
    ILogger<BookDetailsFetcher> logger)
{
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);

    /// <summary>How long the Worker waits between books.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(5);

    public async Task<WorkOutcome> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var providers = catalogs.All.Select(c => c.Provider).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var next = await db.Books
            .Where(b => providers.Contains(b.Provider))
            .Where(IsDue(now))
            .OrderBy(b => b.DetailsCheckedAt != null).ThenBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.Provider, b.CatalogId, b.Title })
            .FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            return WorkOutcome.Idle;
        }

        var catalog = catalogs.All.First(c => c.Provider == next.Provider);
        BookDetails? details;
        try
        {
            details = await catalog.GetDetailsAsync(next.CatalogId, cancellationToken);
        }
        catch (BackendUnavailableException ex)
        {
            LogPaused(ex.Message);
            return WorkOutcome.Paused;
        }
        catch (CatalogLookupFailedException ex)
        {
            LogFailed(next.Title, ex.Message);
            await db.Books.Where(b => b.Id == next.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.DetailsCheckedAt, now).SetProperty(b => b.DetailsFailed, true), cancellationToken);
            return WorkOutcome.Worked;
        }

        // A catalogue that no longer has the book clears nothing already stored: the book itself still stands.
        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == next.Id, cancellationToken);
        if (book is null)
        {
            return WorkOutcome.Worked;
        }

        if (details is not null)
        {
            book.Description = details.Description;
            book.Subjects = [.. details.Subjects];
            book.PageCount = details.PageCount;
            book.Rating = details.Rating;
            book.RatingCount = details.RatingCount;
        }

        book.DetailsCheckedAt = now;
        book.DetailsFailed = false;
        await db.SaveChangesAsync(cancellationToken);
        LogFetched(next.Title, details is not null);
        return WorkOutcome.Worked;
    }

    /// <summary>Never asked, or the last ask failed long enough ago.</summary>
    public static Expression<Func<Book, bool>> IsDue(DateTime now)
    {
        var retryBefore = now - RetryFailedAfter;
        return b => b.DetailsCheckedAt == null || (b.DetailsFailed && b.DetailsCheckedAt <= retryBefore);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Book details for {Title}: {Outcome}")]
    private partial void LogFetchedCore(string title, string outcome);

    private void LogFetched(string title, bool found) => LogFetchedCore(title, found ? "stored" : "the catalogue no longer has it");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Book details for {Title} failed, trying again tomorrow: {Error}")]
    private partial void LogFailed(string title, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Book details paused: {Error}")]
    private partial void LogPaused(string error);
}
