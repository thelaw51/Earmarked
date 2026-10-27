using System.Collections.Concurrent;
using System.Linq.Expressions;
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
/// and a title not found after <see cref="RetryNoMatchAfter"/>, both at the back of the queue. A copied "not found" keeps
/// the time of the search it came from, so every mention of a title comes due together and costs one search.
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

    /// <summary>
    /// How long "not in any catalogue" stands before the title is searched again: catalogues add books (a new release
    /// listed next month, a fallback turned on later). A retry costs what the first search did, once per title: two
    /// requests to each catalogue asked (with the author, then without), fallbacks only for titles with an author
    /// surname, and nothing for a title too vague to match. The adapters' pacing spreads a large batch over time.
    /// </summary>
    public static readonly TimeSpan RetryNoMatchAfter = TimeSpan.FromDays(30);

    /// <summary>Fallbacks whose last answer was a failure, so the log warns once per outage, not once per title.</summary>
    private readonly ConcurrentDictionary<string, bool> _failingFallbacks = new(StringComparer.Ordinal);

    public async Task<WorkOutcome> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var next = await db.BookMentions
            .Where(IsDue(now))
            .OrderBy(m => m.MatchState == MatchState.LookupFailed || m.MatchState == MatchState.NoMatch).ThenBy(m => m.CreatedAt).ThenBy(m => m.Id)
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
        // A "not found" is reused only while it is fresh, or a stale one would be copied instead of searched again.
        var noMatchFreshAfter = now - RetryNoMatchAfter;
        var known = await db.BookMentions
            .Where(m => m.LookupKey == key && m.MatcherVersion == CatalogMatcher.Version)
            .Where(m => (m.MatchState == MatchState.Matched && m.BookId != null) || (m.MatchState == MatchState.NoMatch && m.MatchedAt >= noMatchFreshAfter))
            .Select(m => new { m.MatchState, m.BookId, m.Doubts, m.MatchedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (known is not null)
        {
            // A copied "not found" keeps the time of the search it came from. Stamped with now, two mentions of one title
            // would keep each other fresh by copying in turn, and the title would never be searched again.
            var answeredAt = known.MatchState == MatchState.NoMatch ? known.MatchedAt : null;
            await SaveAsync(db, next.Id, key, new Answer(known.MatchState, known.BookId, known.Doubts, answeredAt), null, cancellationToken);
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
    /// accepted. A primary that can't be reached pauses matching. A fallback that fails, unreachable or with a bad
    /// answer, is skipped and the next one asked, so one optional catalogue being down (or its token expiring) can't
    /// stop the others or the mentions behind this one. If nothing matched and some fallback couldn't be reached, the
    /// title fails and is tried again after <see cref="RetryFailedAfter"/>, since that catalogue might have had it. A
    /// fallback that answered with an error (a rejected token, a bad answer) counts as not having the book: retrying
    /// every such title daily would only spend requests on an error the Status page already shows.
    /// </summary>
    private async Task<Found?> FindAsync(string title, string? author, CancellationToken cancellationToken)
    {
        // No catalogue answer could be accepted, so don't spend a request on it.
        if (CatalogMatcher.IsTooVague(title, author))
        {
            return null;
        }

        CatalogLookupFailedException? fallbackUnreachable = null;
        foreach (var catalog in CatalogMatcher.HasSurname(author) ? catalogs.All : [catalogs.Primary])
        {
            try
            {
                var book = await SearchAsync(catalog, title, author, cancellationToken);
                if (_failingFallbacks.TryRemove(catalog.Provider, out _))
                {
                    LogFallbackRecovered(catalog.Provider);
                }

                if (book is not null)
                {
                    return new Found(book, catalog);
                }
            }
            catch (Exception ex) when (catalog != catalogs.Primary && ex is BackendUnavailableException or CatalogLookupFailedException)
            {
                // Once per outage at Warning; per title it would repeat for every mention in the queue.
                if (_failingFallbacks.TryAdd(catalog.Provider, true))
                {
                    LogFallbackFailing(catalog.Provider, ex.Message);
                }
                else
                {
                    LogFallbackSkipped(catalog.Provider, ex.Message);
                }

                if (ex is BackendUnavailableException)
                {
                    fallbackUnreachable ??= new CatalogLookupFailedException($"Fallback {catalog.Provider} couldn't be reached: {ex.Message}", ex);
                }
            }
        }

        return fallbackUnreachable is null ? null : throw fallbackUnreachable;
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
    /// Mentions the matcher should (re)match: new or matched under older rules, a failed lookup after
    /// <see cref="RetryFailedAfter"/>, and a "not found" after <see cref="RetryNoMatchAfter"/>. The queue and the save
    /// guard share it, so a retried mention can't be picked and then have its answer dropped, which would loop.
    /// </summary>
    private static Expression<Func<BookMention, bool>> IsDue(DateTime now)
    {
        var failedBefore = now - RetryFailedAfter;
        var noMatchBefore = now - RetryNoMatchAfter;
        return m => m.MatcherVersion != CatalogMatcher.Version
            || (m.MatchState == MatchState.LookupFailed && m.MatchedAt < failedBefore)
            || (m.MatchState == MatchState.NoMatch && m.MatchedAt < noMatchBefore);
    }

    /// <summary>
    /// Stores an answer if the mention is still due. With <paramref name="decision"/> null the answer came from the
    /// catalogue or an earlier answer, and is dropped if a decision now exists for the key; otherwise it is dropped if
    /// that decision has since been changed or undone.
    /// </summary>
    private async Task SaveAsync(AppDbContext db, Guid mentionId, string key, Answer answer, ReviewDecision? decision, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = db.BookMentions.Where(m => m.Id == mentionId).Where(IsDue(now));
        due = decision is null
            ? due.Where(m => !db.ReviewDecisions.Any(d => d.LookupKey == key))
            : due.Where(m => db.ReviewDecisions.Any(d => d.Id == decision.Id && d.DecidedAt == decision.DecidedAt));
        await due.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.MatchState, answer.State)
            .SetProperty(m => m.BookId, answer.BookId)
            .SetProperty(m => m.Doubts, answer.Doubts)
            .SetProperty(m => m.LookupKey, key)
            .SetProperty(m => m.MatcherVersion, CatalogMatcher.Version)
            .SetProperty(m => m.MatchedAt, answer.AnsweredAt ?? now), cancellationToken);
    }

    /// <summary>A match result to save. <paramref name="AnsweredAt"/> is when a copied answer was found, if not now.</summary>
    private sealed record Answer(MatchState State, Guid? BookId, MatchDoubts Doubts, DateTime? AnsweredAt = null);

    private sealed record Found(CatalogBook Book, IBookCatalog Catalog);

    [LoggerMessage(Level = LogLevel.Information, Message = "Matched \"{Title}\" to \"{Book}\"")]
    private partial void LogMatched(string title, string book);

    [LoggerMessage(Level = LogLevel.Information, Message = "No catalogue match for \"{Title}\"")]
    private partial void LogNoMatch(string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Catalogue lookup failed for \"{Title}\": {Error}")]
    private partial void LogLookupFailed(string title, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't apply the {Verdict} review decision for \"{Title}\"")]
    private partial void LogDecisionUnusable(string title, ReviewVerdict verdict);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fallback catalogue {Catalogue} is failing, so titles skip it until it answers again: {Reason}")]
    private partial void LogFallbackFailing(string catalogue, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fallback catalogue {Catalogue} failed, trying the next: {Reason}")]
    private partial void LogFallbackSkipped(string catalogue, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fallback catalogue {Catalogue} is answering again")]
    private partial void LogFallbackRecovered(string catalogue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Book matching paused: {Reason}")]
    private partial void LogPaused(string reason);
}
