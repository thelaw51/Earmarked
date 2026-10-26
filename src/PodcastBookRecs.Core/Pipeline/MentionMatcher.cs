using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>
/// Matches book mentions to catalogue books, one mention at a time, oldest first. A mention whose title and author were
/// already looked up under the current rules copies that answer instead of asking again. Mentions not yet matched by
/// <see cref="CatalogMatcher.Version"/>, including every new one, are due, so changing the rules re-matches everything.
/// A lookup that failed is tried again after <see cref="RetryFailedAfter"/>, at the back of the queue.
/// Mentions aren't episode state, so this doesn't take <see cref="EpisodeStateLock"/>: each update is conditional, and a
/// mention deleted by re-extraction while it was being looked up simply updates nothing.
/// </summary>
public sealed partial class MentionMatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IBookCatalog catalog,
    TimeProvider time,
    ILogger<MentionMatcher> logger)
{
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromDays(1);

    public async Task<WorkOutcome> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var retryBefore = time.GetUtcNow().UtcDateTime - RetryFailedAfter;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var next = await db.BookMentions
            .Where(m => m.MatcherVersion != CatalogMatcher.Version || (m.MatchState == MatchState.LookupFailed && m.MatchedAt < retryBefore))
            .OrderBy(m => m.MatchState == MatchState.LookupFailed).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => new { m.Id, m.Title, m.Author })
            .FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            return WorkOutcome.Idle;
        }

        var key = CatalogMatcher.LookupKey(next.Title, next.Author);
        var known = await db.BookMentions
            .Where(m => m.LookupKey == key && m.MatcherVersion == CatalogMatcher.Version)
            .Where(m => (m.MatchState == MatchState.Matched && m.BookId != null) || m.MatchState == MatchState.NoMatch)
            .Select(m => new { m.MatchState, m.BookId })
            .FirstOrDefaultAsync(cancellationToken);
        if (known is not null)
        {
            await SaveAsync(db, next.Id, key, known.MatchState, known.BookId, cancellationToken);
            return WorkOutcome.Worked;
        }

        CatalogBook? book;
        try
        {
            book = await FindAsync(next.Title, next.Author, cancellationToken);
        }
        catch (BackendUnavailableException ex)
        {
            LogPaused(ex.Message);
            return WorkOutcome.Paused;
        }
        catch (CatalogLookupFailedException ex)
        {
            LogLookupFailed(next.Title, ex.Message);
            await SaveAsync(db, next.Id, key, MatchState.LookupFailed, null, cancellationToken);
            return WorkOutcome.Worked;
        }

        if (book is null)
        {
            await SaveAsync(db, next.Id, key, MatchState.NoMatch, null, cancellationToken);
            LogNoMatch(next.Title);
        }
        else
        {
            var bookId = await AddBookAsync(db, book, cancellationToken);
            await SaveAsync(db, next.Id, key, MatchState.Matched, bookId, cancellationToken);
            LogMatched(next.Title, book.Title);
        }

        return WorkOutcome.Worked;
    }

    /// <summary>
    /// Searches with the author when the model gave one, then by title alone, because catalogues file authors in ways
    /// the model doesn't repeat ("J. R. R." against "J.R.R."). <see cref="CatalogMatcher"/> still checks the surname.
    /// </summary>
    private async Task<CatalogBook?> FindAsync(string title, string? author, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(author)
            && CatalogMatcher.Pick(title, author, await catalog.SearchAsync(title, author, cancellationToken)) is { } withAuthor)
        {
            return withAuthor;
        }

        return CatalogMatcher.Pick(title, author, await catalog.SearchAsync(title, null, cancellationToken));
    }

    /// <summary>Returns the stored book for this catalogue entry, adding it first if it's new. Only this loop adds books.</summary>
    private async Task<Guid> AddBookAsync(AppDbContext db, CatalogBook found, CancellationToken cancellationToken)
    {
        var existing = await db.Books.Where(b => b.Provider == catalog.Provider && b.CatalogId == found.CatalogId)
            .Select(b => (Guid?)b.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is { } id)
        {
            return id;
        }

        var authors = string.Join(", ", found.Authors);
        var book = new Book
        {
            Provider = catalog.Provider,
            CatalogId = found.CatalogId,
            Title = found.Title,
            Authors = authors.Length <= Book.MaxAuthorsLength ? authors : authors[..Book.MaxAuthorsLength],
            FirstPublishYear = found.FirstPublishYear,
            InfoUrl = found.InfoUrl,
            CoverUrl = found.CoverUrl,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        return book.Id;
    }

    private async Task SaveAsync(AppDbContext db, Guid mentionId, string key, MatchState state, Guid? bookId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.BookMentions
            .Where(m => m.Id == mentionId && (m.MatcherVersion != CatalogMatcher.Version || m.MatchState == MatchState.LookupFailed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.MatchState, state)
                .SetProperty(m => m.BookId, bookId)
                .SetProperty(m => m.LookupKey, key)
                .SetProperty(m => m.MatcherVersion, CatalogMatcher.Version)
                .SetProperty(m => m.MatchedAt, now), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Matched \"{Title}\" to \"{Book}\"")]
    private partial void LogMatched(string title, string book);

    [LoggerMessage(Level = LogLevel.Information, Message = "No catalogue match for \"{Title}\"")]
    private partial void LogNoMatch(string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalogue lookup failed for \"{Title}\": {Error}")]
    private partial void LogLookupFailed(string title, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Book matching paused: {Reason}")]
    private partial void LogPaused(string reason);
}
