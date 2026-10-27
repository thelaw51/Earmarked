using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>
/// Matches book mentions to catalogue books, one mention at a time, oldest first. A person's <see cref="ReviewDecision"/>
/// for the mention's lookup key wins over everything else and is applied without asking the catalogue. Otherwise a
/// mention whose title and author were already looked up under the current rules copies that answer instead of asking
/// again. Mentions not yet matched by <see cref="CatalogMatcher.Version"/>, including every new one, are due, so
/// changing the rules re-matches everything. A lookup that failed is tried again after <see cref="RetryFailedAfter"/>,
/// at the back of the queue.
/// Mentions aren't episode state, so this doesn't take <see cref="EpisodeStateLock"/>: each update is conditional, and a
/// mention deleted by re-extraction while it was being looked up simply updates nothing. A save from the catalogue also
/// updates nothing once a decision exists for its key, and a save from a decision updates nothing once that decision
/// has changed, so neither can overwrite a newer ruling. The mention stays due and the next pass gets it right.
/// </summary>
public sealed partial class MentionMatcher(
    IDbContextFactory<AppDbContext> dbFactory,
    BookCatalogChain catalogs,
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
        var decision = await db.ReviewDecisions.AsNoTracking().Where(d => d.LookupKey == key).FirstOrDefaultAsync(cancellationToken);
        if (decision is not null)
        {
            await ApplyAsync(db, next.Id, key, next.Title, decision, cancellationToken);
            return WorkOutcome.Worked;
        }

        // Mentions settled by a decision share this key, so this can reuse their answer. That is safe only because the
        // decision check above runs first. After an undo, anything reused before ReviewDecided runs is cleared when it does.
        var known = await db.BookMentions
            .Where(m => m.LookupKey == key && m.MatcherVersion == CatalogMatcher.Version)
            .Where(m => (m.MatchState == MatchState.Matched && m.BookId != null) || m.MatchState == MatchState.NoMatch)
            .Select(m => new { m.MatchState, m.BookId, m.Doubts })
            .FirstOrDefaultAsync(cancellationToken);
        if (known is not null)
        {
            await SaveAsync(db, next.Id, key, new Answer(known.MatchState, known.BookId, known.Doubts), null, cancellationToken);
            return WorkOutcome.Worked;
        }

        Found? found;
        try
        {
            found = await FindAsync(next.Title, next.Author, cancellationToken);
        }
        catch (BackendUnavailableException ex)
        {
            LogPaused(ex.Message);
            return WorkOutcome.Paused;
        }
        catch (CatalogLookupFailedException ex)
        {
            LogLookupFailed(next.Title, ex.Message);
            await SaveAsync(db, next.Id, key, new Answer(MatchState.LookupFailed, null, MatchDoubts.None), null, cancellationToken);
            return WorkOutcome.Worked;
        }

        if (found is null)
        {
            await SaveAsync(db, next.Id, key, new Answer(MatchState.NoMatch, null, MatchDoubts.None), null, cancellationToken);
            LogNoMatch(next.Title);
        }
        else
        {
            // Credit the catalogue that was asked, whatever the entry says, so a search result can never pass as Manual.
            var (book, catalog) = found;
            var bookId = await AddBookAsync(db, book with { Provider = catalog.Provider }, cancellationToken);
            var doubts = CatalogMatcher.Doubts(next.Author, book) | (catalog == catalogs.Primary ? MatchDoubts.None : MatchDoubts.OtherCatalogue);
            await SaveAsync(db, next.Id, key, new Answer(MatchState.Matched, bookId, doubts), null, cancellationToken);
            LogMatched(next.Title, book.Title);
        }

        return WorkOutcome.Worked;
    }

    /// <summary>
    /// Applies a person's decision to one mention. A decision that can't be applied (a confirmed book that's gone, or a
    /// picked book that is missing or doesn't look like a catalogue entry) marks the mention <see cref="MatchState.LookupFailed"/>
    /// so it can't block the queue; it's tried again after <see cref="RetryFailedAfter"/>.
    /// </summary>
    private async Task ApplyAsync(AppDbContext db, Guid mentionId, string key, string title, ReviewDecision decision, CancellationToken cancellationToken)
    {
        var failed = new Answer(MatchState.LookupFailed, null, MatchDoubts.None);
        var answer = decision.Verdict switch
        {
            ReviewVerdict.NotABook or ReviewVerdict.WrongBook => new Answer(MatchState.Rejected, null, MatchDoubts.None),
            ReviewVerdict.Confirmed when decision.BookId is { } id => new Answer(MatchState.Matched, id, MatchDoubts.None),
            ReviewVerdict.Corrected when decision.BookId is { } id => new Answer(MatchState.Matched, id, MatchDoubts.None),
            ReviewVerdict.Corrected when CatalogMatcher.IsUsable(decision.Pick) && decision.Pick!.ProviderOrDefault != BookLookupProviders.Manual
                => new Answer(MatchState.Matched, await AddPickAsync(db, decision, cancellationToken), MatchDoubts.None),
            ReviewVerdict.KeptAsIs when decision.BookId is { } id => new Answer(MatchState.Matched, id, MatchDoubts.None),
            ReviewVerdict.KeptAsIs when IsOwnManualPick(decision) => new Answer(MatchState.Matched, await AddPickAsync(db, decision, cancellationToken), MatchDoubts.None),
            _ => failed,
        };

        if (answer == failed)
        {
            LogDecisionUnusable(title, decision.Verdict);
        }

        await SaveAsync(db, mentionId, key, answer, decision, cancellationToken);
    }

    /// <summary>
    /// A kept-as-is pick is a <see cref="BookLookupProviders.Manual"/> entry whose ID is its own decision's, so one
    /// decision can never point its mentions at the book another decision added.
    /// </summary>
    private static bool IsOwnManualPick(ReviewDecision decision) =>
        CatalogMatcher.IsUsable(decision.Pick)
        && decision.Pick!.Provider == BookLookupProviders.Manual
        && decision.Pick.CatalogId == decision.Id.ToString("N");

    /// <summary>Adds the picked book, records it on the decision so later mentions reuse it, and returns its ID.</summary>
    private async Task<Guid> AddPickAsync(AppDbContext db, ReviewDecision decision, CancellationToken cancellationToken)
    {
        var bookId = await AddBookAsync(db, decision.Pick!, cancellationToken);
        await db.ReviewDecisions.Where(d => d.Id == decision.Id && d.DecidedAt == decision.DecidedAt && d.BookId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.BookId, bookId), cancellationToken);
        return bookId;
    }

    /// <summary>
    /// Asks the primary catalogue, then each fallback in turn while nothing is found. Fallbacks are asked only when the
    /// model named an author: they don't count editions, so without a surname to check no answer of theirs could be
    /// accepted. A primary that can't be reached pauses matching. A fallback that can't be reached fails only this
    /// title, which is tried again after <see cref="RetryFailedAfter"/>, because an optional catalogue being down
    /// mustn't stop the mentions behind this one, most of which the primary can match.
    /// </summary>
    private async Task<Found?> FindAsync(string title, string? author, CancellationToken cancellationToken)
    {
        // No catalogue answer could be accepted, so don't spend a request on it.
        if (CatalogMatcher.IsTooVague(title, author))
        {
            return null;
        }

        foreach (var catalog in CatalogMatcher.HasSurname(author) ? catalogs.All : [catalogs.Primary])
        {
            try
            {
                if (await SearchAsync(catalog, title, author, cancellationToken) is { } book)
                {
                    return new Found(book, catalog);
                }
            }
            catch (BackendUnavailableException ex) when (catalog != catalogs.Primary)
            {
                throw new CatalogLookupFailedException($"Fallback {catalog.Provider} unavailable: {ex.Message}", ex);
            }
        }

        return null;
    }

    /// <summary>
    /// Searches with the author when the model gave one, then by title alone, because catalogues file authors in ways
    /// the model doesn't repeat ("J. R. R." against "J.R.R."). <see cref="CatalogMatcher"/> still checks the surname.
    /// </summary>
    private static async Task<CatalogBook?> SearchAsync(IBookCatalog catalog, string title, string? author, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(author)
            && CatalogMatcher.Pick(title, author, await catalog.SearchAsync(title, author, cancellationToken)) is { } withAuthor)
        {
            return withAuthor;
        }

        return CatalogMatcher.Pick(title, author, await catalog.SearchAsync(title, null, cancellationToken));
    }

    /// <summary>
    /// Returns the stored book for this catalogue entry, adding it first if it's new. Only this loop adds books. A
    /// <see cref="BookLookupProviders.Manual"/> book takes the entry's title and authors each time, because its ID is
    /// its decision's and the person may have kept the same title again with corrected text.
    /// </summary>
    private async Task<Guid> AddBookAsync(AppDbContext db, CatalogBook found, CancellationToken cancellationToken)
    {
        var provider = found.ProviderOrDefault;
        var authors = string.Join(", ", found.Authors);
        authors = authors.Length <= Book.MaxAuthorsLength ? authors : authors[..Book.MaxAuthorsLength];
        var existing = await db.Books.Where(b => b.Provider == provider && b.CatalogId == found.CatalogId)
            .Select(b => (Guid?)b.Id).FirstOrDefaultAsync(cancellationToken);
        if (existing is { } id)
        {
            if (provider == BookLookupProviders.Manual)
            {
                await db.Books.Where(b => b.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Title, found.Title)
                    .SetProperty(b => b.Authors, authors), cancellationToken);
            }

            return id;
        }

        var book = new Book
        {
            Provider = provider,
            CatalogId = found.CatalogId,
            Title = found.Title,
            Authors = authors,
            FirstPublishYear = found.FirstPublishYear,
            InfoUrl = found.InfoUrl,
            CoverUrl = found.CoverUrl,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        return book.Id;
    }

    /// <summary>
    /// Stores an answer if the mention is still due. With <paramref name="decision"/> null the answer came from the
    /// catalogue or an earlier answer, and is dropped if a decision now exists for the key; otherwise it is dropped if
    /// that decision has since been changed or undone.
    /// </summary>
    private async Task SaveAsync(AppDbContext db, Guid mentionId, string key, Answer answer, ReviewDecision? decision, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = db.BookMentions
            .Where(m => m.Id == mentionId && (m.MatcherVersion != CatalogMatcher.Version || m.MatchState == MatchState.LookupFailed));
        due = decision is null
            ? due.Where(m => !db.ReviewDecisions.Any(d => d.LookupKey == key))
            : due.Where(m => db.ReviewDecisions.Any(d => d.Id == decision.Id && d.DecidedAt == decision.DecidedAt));
        await due.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.MatchState, answer.State)
            .SetProperty(m => m.BookId, answer.BookId)
            .SetProperty(m => m.Doubts, answer.Doubts)
            .SetProperty(m => m.LookupKey, key)
            .SetProperty(m => m.MatcherVersion, CatalogMatcher.Version)
            .SetProperty(m => m.MatchedAt, now), cancellationToken);
    }

    private sealed record Answer(MatchState State, Guid? BookId, MatchDoubts Doubts);

    private sealed record Found(CatalogBook Book, IBookCatalog Catalog);

    [LoggerMessage(Level = LogLevel.Information, Message = "Matched \"{Title}\" to \"{Book}\"")]
    private partial void LogMatched(string title, string book);

    [LoggerMessage(Level = LogLevel.Information, Message = "No catalogue match for \"{Title}\"")]
    private partial void LogNoMatch(string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalogue lookup failed for \"{Title}\": {Error}")]
    private partial void LogLookupFailed(string title, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't apply the {Verdict} review decision for \"{Title}\"")]
    private partial void LogDecisionUnusable(string title, ReviewVerdict verdict);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Book matching paused: {Reason}")]
    private partial void LogPaused(string reason);
}
