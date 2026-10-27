using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>
/// Runs the catalogue searches people ask for on the review page (<see cref="CatalogSearch"/>), oldest first. It asks
/// every catalogue in the same <see cref="BookCatalogChain"/> as the matcher, so each adapter's pacing covers both.
/// The catalogues are asked at once, since each paces only its own requests, and their answers are listed in chain
/// order, each marked with the catalogue that gave it. Someone is waiting for each answer, so a failure completes the
/// search with a message instead of pausing and trying again. A catalogue that fails or runs past
/// <see cref="SearchTimeout"/> while another answers is left out, and <see cref="PartialError"/> says the list may be short.
/// </summary>
public sealed partial class CatalogSearchRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    BookCatalogChain catalogs,
    TimeProvider time,
    ILogger<CatalogSearchRunner> logger)
{
    /// <summary>The review page stops waiting after 30 seconds, so a search this old has nobody waiting for it.</summary>
    public static readonly TimeSpan ExpireAfter = TimeSpan.FromMinutes(2);

    /// <summary>A pick copies its result into the decision, so searches are only needed while someone is choosing.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    public const string ExpiredError = "The search took too long. Try again.";
    public const string UnavailableError = "The catalogue can't be reached right now. Try again in a minute.";
    public const string UnreadableError = "The catalogue's answer couldn't be read.";
    public const string FailedError = "The search failed. The Worker's log has the details.";
    public const string PartialError = "Not every catalogue could be searched, so this list may be short. Try again in a minute.";

    /// <summary>Results kept from each catalogue, so one can't crowd out the others.</summary>
    public const int MaxResultsPerCatalogue = 10;

    /// <summary>How often searches older than <see cref="KeepFor"/> are deleted.</summary>
    private static readonly TimeSpan CleanUpEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// How long one search may take across every catalogue, kept under the review page's 30-second wait so the page
    /// gets whatever did answer. Tests shorten it.
    /// </summary>
    internal TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(25);

    private DateTime _nextCleanUp = DateTime.MinValue;

    /// <summary>
    /// Answers open searches, closing any nobody is waiting for, and now and then deletes old ones. It runs every second,
    /// so a pass with nothing open only reads: a write, even one that changes no rows, takes SQLite's write lock, which
    /// the matcher, extractor and poller need.
    /// </summary>
    public async Task RunPendingAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        List<CatalogSearch> open;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            open = await db.CatalogSearches.AsNoTracking().Where(s => s.CompletedAt == null).OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken);
            if (now >= _nextCleanUp)
            {
                var keepAfter = now - KeepFor;
                await db.CatalogSearches.Where(s => s.CreatedAt < keepAfter).ExecuteDeleteAsync(cancellationToken);
                _nextCleanUp = now + CleanUpEvery;
            }
        }

        foreach (var search in open)
        {
            if (search.CreatedAt < now - ExpireAfter)
            {
                await CompleteAsync(search.Id, null, ExpiredError, cancellationToken);
            }
            else
            {
                await RunAsync(search, cancellationToken);
            }
        }
    }

    private async Task RunAsync(CatalogSearch search, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(SearchTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var answers = await Task.WhenAll(catalogs.All.Select(catalog => AskAsync(catalog, search, linked.Token, cancellationToken)));

        var answered = answers.Where(a => a.Results is not null).ToList();
        var error = answered.Count == answers.Length ? null
            : answered.Count > 0 ? PartialError
            : answers.Select(a => a.Error).First(e => e is not null);
        await CompleteAsync(search.Id, answered.Count > 0 ? [.. answered.SelectMany(a => a.Results!)] : null, error, cancellationToken);
    }

    /// <summary>
    /// One catalogue's answer, by title and author and then by title alone, or the fixed message for why it has none.
    /// Only <paramref name="stopping"/> (the Worker shutting down) escapes as an exception; running past the search's
    /// time limit counts as the catalogue being unavailable.
    /// </summary>
    private async Task<(List<CatalogBook>? Results, string? Error)> AskAsync(
        IBookCatalog catalog, CatalogSearch search, CancellationToken cancellationToken, CancellationToken stopping)
    {
        try
        {
            var found = await catalog.SearchAsync(search.Title, search.Author, cancellationToken);
            if (found.Count == 0 && !string.IsNullOrEmpty(search.Author))
            {
                found = await catalog.SearchAsync(search.Title, null, cancellationToken);
            }

            // Credit the catalogue that was asked, so a pick carries it into the decision and can never pass as Manual.
            return ([.. found.Take(MaxResultsPerCatalogue).Select(b => b with { Provider = catalog.Provider })], null);
        }
        catch (BackendUnavailableException ex)
        {
            LogSearchUnavailable(catalog.Provider, ex.Message);
            return (null, UnavailableError);
        }
        catch (CatalogLookupFailedException ex)
        {
            LogSearchUnreadable(catalog.Provider, ex.Message);
            return (null, UnreadableError);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            LogSearchTimedOut(catalog.Provider);
            return (null, UnavailableError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSearchFailed(ex, catalog.Provider);
            return (null, FailedError);
        }
    }

    private async Task CompleteAsync(Guid id, List<CatalogBook>? results, string? error, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.CatalogSearches.Where(s => s.Id == id && s.CompletedAt == null).ExecuteUpdateAsync(
            s => s.SetProperty(c => c.CompletedAt, now).SetProperty(c => c.Error, error).SetProperty(c => c.Results, results),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A review search couldn't reach {Catalogue}: {Reason}")]
    private partial void LogSearchUnavailable(string catalogue, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A review search got an unusable answer from {Catalogue}: {Reason}")]
    private partial void LogSearchUnreadable(string catalogue, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "A review search ran out of time waiting for {Catalogue}")]
    private partial void LogSearchTimedOut(string catalogue);

    [LoggerMessage(Level = LogLevel.Error, Message = "A review search failed in {Catalogue}")]
    private partial void LogSearchFailed(Exception ex, string catalogue);
}
