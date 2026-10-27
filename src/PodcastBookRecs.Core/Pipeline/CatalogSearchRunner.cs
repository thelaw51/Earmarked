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

    /// <summary>Clears out old searches, closes ones nobody is waiting for, then answers the rest.</summary>
    public async Task RunPendingAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var keepAfter = now - KeepFor;
        var expireBefore = now - ExpireAfter;
        List<CatalogSearch> open;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await db.CatalogSearches.Where(s => s.CreatedAt < keepAfter).ExecuteDeleteAsync(cancellationToken);
            await db.CatalogSearches.Where(s => s.CompletedAt == null && s.CreatedAt < expireBefore).ExecuteUpdateAsync(
                s => s.SetProperty(c => c.CompletedAt, now).SetProperty(c => c.Error, ExpiredError), cancellationToken);
            open = await db.CatalogSearches.AsNoTracking().Where(s => s.CompletedAt == null).OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken);
        }

        foreach (var search in open)
        {
            await RunAsync(search, cancellationToken);
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

        var now = time.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.CatalogSearches.Where(s => s.Id == search.Id && s.CompletedAt == null).ExecuteUpdateAsync(
            s => s.SetProperty(c => c.CompletedAt, now).SetProperty(c => c.Error, error).SetProperty(c => c.Results, error == null ? results : null),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A review search couldn't reach the catalogue: {Reason}")]
    private partial void LogSearchUnavailable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A review search got an unusable answer from the catalogue: {Reason}")]
    private partial void LogSearchUnreadable(string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A review search failed")]
    private partial void LogSearchFailed(Exception ex);
}
