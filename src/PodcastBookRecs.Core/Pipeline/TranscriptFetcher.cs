using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>
/// Downloads and parses the published transcript for the next <see cref="EpisodeState.New"/> episode.
/// Links are tried best-format first. A link that is gone or unparseable moves on to the next one; when none
/// work the episode drops to AwaitingTranscription for the transcription service. Transient errors retry with backoff.
/// </summary>
public sealed partial class TranscriptFetcher(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    IOptions<PollingOptions> polling,
    TimeProvider time,
    ILogger<TranscriptFetcher> logger)
{
    private const int MaxTranscriptBytes = 32 * 1024 * 1024;

    /// <summary>Processes one episode. Returns false when nothing is due, so the caller can wait.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var episode = await db.Episodes
            .Where(e => e.State == EpisodeState.New && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
            .OrderBy(e => e.PrioritisedAt == null)
            .ThenBy(e => e.PrioritisedAt)
            .ThenByDescending(e => e.PublishedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (episode is null)
        {
            return false;
        }

        var candidates = episode.TranscriptLinks
            .Select(l => (Link: l, Format: TranscriptLinkSelector.DetectFormat(l.Type, l.Url)))
            .Where(c => c.Format != TranscriptFormat.Unknown)
            .OrderBy(c => c.Format)
            .ToList();

        var errors = new List<string>();
        foreach (var (link, format) in candidates)
        {
            try
            {
                var content = await DownloadAsync(link.Url, cancellationToken);
                var segments = TranscriptParser.Parse(content, format);
                db.Transcripts.Add(new Transcript
                {
                    EpisodeId = episode.Id,
                    Source = TranscriptSource.Feed,
                    Format = format,
                    SourceUrl = link.Url,
                    CreatedAt = now,
                    Segments = segments,
                });
                Complete(episode, EpisodeState.TranscriptFetched, null, now);
                await db.SaveChangesAsync(cancellationToken);
                LogFetched(episode.Title, format, segments.Count);
                return true;
            }
            catch (PermanentFetchException ex)
            {
                errors.Add($"{format}: {ex.Message}");
            }
            catch (TranscriptFormatException ex)
            {
                errors.Add($"{format}: {ex.Message}");
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                var message = ex is TaskCanceledException ? "Timed out downloading the transcript." : ex.Message;
                episode.Attempts++;
                if (episode.Attempts >= polling.Value.MaxAttempts)
                {
                    Complete(episode, EpisodeState.AwaitingTranscription, $"Gave up after {episode.Attempts} attempts: {message}", now);
                }
                else
                {
                    episode.NextAttemptAt = now + Backoff(episode.Attempts);
                    episode.LastError = message;
                    episode.UpdatedAt = now;
                }

                await db.SaveChangesAsync(cancellationToken);
                LogTransient(episode.Title, episode.Attempts, message);
                return true;
            }
        }

        Complete(episode, EpisodeState.AwaitingTranscription, errors.Count > 0 ? string.Join(" | ", errors) : "No transcript link in a supported format.", now);
        await db.SaveChangesAsync(cancellationToken);
        LogFallback(episode.Title, episode.LastError!);
        return true;
    }

    /// <summary>Exponential backoff: 2, 4, 8 … minutes, capped at 6 hours.</summary>
    internal static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Min(Math.Pow(2, attempts), 360));

    private async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await httpFactory.CreateClient(ServiceCollectionExtensions.FeedHttpClient)
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var status = (int)response.StatusCode;
        if (status is >= 400 and < 500 && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
        {
            throw new PermanentFetchException($"HTTP {status}");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxTranscriptBytes)
        {
            throw new PermanentFetchException("Transcript is too large.");
        }

        // Content-Length can be absent (chunked responses), so cap the bytes actually read as well.
        await response.Content.LoadIntoBufferAsync(MaxTranscriptBytes, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static void Complete(Episode episode, EpisodeState state, string? error, DateTime now)
    {
        episode.State = state;
        episode.Attempts = 0;
        episode.NextAttemptAt = null;
        episode.LastError = error;
        episode.UpdatedAt = now;
    }

    private sealed class PermanentFetchException(string message) : Exception(message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetched {Format} transcript for {Episode}: {Segments} segments")]
    private partial void LogFetched(string episode, TranscriptFormat format, int segments);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcript download for {Episode} failed (attempt {Attempt}): {Error}")]
    private partial void LogTransient(string episode, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "No usable published transcript for {Episode}, waiting for transcription: {Reason}")]
    private partial void LogFallback(string episode, string reason);
}
