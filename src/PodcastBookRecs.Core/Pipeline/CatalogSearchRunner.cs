using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>
/// Runs the catalogue searches people ask for on the review page (<see cref="CatalogSearch"/>), oldest first. It uses
/// the same <see cref="IBookCatalog"/> as the matcher, so the adapter's pacing covers both. Someone is waiting for each
/// answer, so a failure completes the search with a message instead of pausing and trying again.
/// </summary>
public sealed partial class CatalogSearchRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    IBookCatalog catalog,
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

    /// <summary>How often searches older than <see cref="KeepFor"/> are deleted.</summary>
    private static readonly TimeSpan CleanUpEvery = TimeSpan.FromHours(1);

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
        List<CatalogBook>? results = null;
        string? error = null;
        try
        {
            results = [.. await catalog.SearchAsync(search.Title, search.Author, cancellationToken)];
            if (results.Count == 0 && !string.IsNullOrEmpty(search.Author))
            {
                results = [.. await catalog.SearchAsync(search.Title, null, cancellationToken)];
            }
        }
        catch (BackendUnavailableException ex)
        {
            LogSearchUnavailable(ex.Message);
            error = UnavailableError;
        }
        catch (CatalogLookupFailedException ex)
        {
            LogSearchUnreadable(ex.Message);
            error = UnreadableError;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSearchFailed(ex);
            error = FailedError;
        }

        await CompleteAsync(search.Id, error == null ? results : null, error, cancellationToken);
    }

    private async Task CompleteAsync(Guid id, List<CatalogBook>? results, string? error, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.CatalogSearches.Where(s => s.Id == id && s.CompletedAt == null).ExecuteUpdateAsync(
            s => s.SetProperty(c => c.CompletedAt, now).SetProperty(c => c.Error, error).SetProperty(c => c.Results, results),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A review search couldn't reach the catalogue: {Reason}")]
    private partial void LogSearchUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A review search got an unusable answer from the catalogue: {Reason}")]
    private partial void LogSearchUnreadable(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A review search failed")]
    private partial void LogSearchFailed(Exception ex);
}
