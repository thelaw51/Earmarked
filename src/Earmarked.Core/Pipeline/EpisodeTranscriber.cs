using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Earmarked.Core.Audio;
using Earmarked.Core.Backends;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Transcription;
using Earmarked.Core.Transcripts;

namespace Earmarked.Core.Pipeline;

/// <summary>What one call to a worker such as <see cref="EpisodeTranscriber.ProcessNextAsync"/> did, so the caller knows how long to wait.</summary>
public enum WorkOutcome
{
    /// <summary>Nothing was due.</summary>
    Idle,

    /// <summary>An episode was processed, or a failure was recorded against it. Look for the next one straight away.</summary>
    Worked,

    /// <summary>The backend couldn't be reached, or the disk is too full. Nothing was counted against the episode; back off.</summary>
    Paused,
}

/// <summary>
/// Transcribes one episode at a time: download the audio, split it with ffmpeg, send each chunk to the
/// transcription backend, then store the joined transcript. Every finished chunk is saved to the episode's work
/// folder, so a restart or an unreachable backend resumes where it stopped instead of starting again.
/// </summary>
public sealed partial class EpisodeTranscriber(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    ITranscriptionService transcription,
    IAudioChunker chunker,
    EpisodeStateLock stateLock,
    TranscriptionWorkspace workspace,
    IOptions<TranscriptionOptions> options,
    IOptions<PollingOptions> polling,
    TimeProvider time,
    ILogger<EpisodeTranscriber> logger)
{
    /// <summary>Downloads above this size are refused. A 4.5 hour MP3 is roughly 250 to 400 MB.</summary>
    internal const long MaxAudioBytes = 1536L * 1024 * 1024;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkOutcome> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var episode = await ClaimNextAsync(cancellationToken);
        if (episode is null)
        {
            return WorkOutcome.Idle;
        }

        try
        {
            return await TranscribeAsync(episode, cancellationToken);
        }
        catch (BackendUnavailableException ex)
        {
            LogPaused(ex.Message);
            return WorkOutcome.Paused;
        }
        catch (InsufficientDiskSpaceException ex)
        {
            LogPaused(ex.Message);
            return WorkOutcome.Paused;
        }
        catch (AudioDownloadException ex) when (ex.LinkMayHaveExpired)
        {
            // Signed links (e.g. Patreon) expire. Wait for the next poll, which refreshes the stored URL.
            var nextPoll = (episode.PodcastFeed.LastPolledAt ?? time.GetUtcNow().UtcDateTime).AddMinutes(polling.Value.IntervalMinutes + 5);
            await RecordFailureAsync(episode, ex.Message, notBefore: nextPoll, cancellationToken);
            return WorkOutcome.Worked;
        }
        catch (Exception ex) when (ex is TranscriptionFailedException or AudioProcessingException or AudioDownloadException or IOException)
        {
            await RecordFailureAsync(episode, ex.Message, notBefore: null, cancellationToken);
            return WorkOutcome.Worked;
        }
    }

    /// <summary>Deletes work folders left behind by episodes that are no longer being transcribed (reset, failed, or deleted).</summary>
    public async Task CleanUpOrphansAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var active = (await db.Episodes.Where(e => e.State == EpisodeState.Transcribing).Select(e => e.Id).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var id in workspace.Existing().Where(id => !active.Contains(id)))
        {
            workspace.Delete(id);
        }
    }

    /// <summary>
    /// Picks the next due episode: one already being transcribed first (to resume), then prioritised ones in the order
    /// they were asked for, then the newest waiting one. Shows that have published transcripts before get a grace
    /// period first, unless the episode is prioritised. Claiming is a conditional update under
    /// the state lock, so a poll that just requeued the episode for a published transcript wins.
    /// </summary>
    internal async Task<Episode?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        using var _ = await stateLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var candidate = await db.Episodes
            .Where(IsDue(now, options.Value))
            .OrderBy(e => e.State == EpisodeState.Transcribing ? 0 : 1)
            .ThenBy(e => e.PrioritisedAt == null)
            .ThenBy(e => e.PrioritisedAt)
            .ThenByDescending(e => e.PublishedAt)
            .Select(e => new { e.Id, e.State })
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        if (candidate.State == EpisodeState.AwaitingTranscription)
        {
            var claimed = await db.Episodes
                .Where(e => e.Id == candidate.Id && e.State == EpisodeState.AwaitingTranscription)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.State, EpisodeState.Transcribing).SetProperty(e => e.UpdatedAt, now), cancellationToken);
            if (claimed != 1)
            {
                return null;
            }
        }

        return await db.Episodes.AsNoTracking().Include(e => e.PodcastFeed).SingleAsync(e => e.Id == candidate.Id, cancellationToken);
    }

    /// <summary>
    /// Whether the transcriber would take the episode now. <see cref="EpisodeExtractor"/> uses the same rule to see
    /// whether transcription has work waiting.
    /// </summary>
    internal static Expression<Func<Episode, bool>> IsDue(DateTime now, TranscriptionOptions options)
    {
        var graceCutoff = now.AddDays(-options.PublishedTranscriptGraceDays);
        return e => e.EnclosureUrl != null && (e.NextAttemptAt == null || e.NextAttemptAt <= now)
            && (e.State == EpisodeState.Transcribing
                || (e.State == EpisodeState.AwaitingTranscription
                    && (e.PrioritisedAt != null || !e.PodcastFeed.PublishesTranscripts || e.PublishedAt == null || e.PublishedAt <= graceCutoff)));
    }

    private async Task<WorkOutcome> TranscribeAsync(Episode episode, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var dir = workspace.For(episode.Id);
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source.audio");

        if (!File.Exists(source))
        {
            EnsureFreeSpace();
            await DownloadAsync(episode.EnclosureUrl!, source, cancellationToken);
        }

        EnsureFreeSpace();
        var chunks = await chunker.SplitAsync(source, dir, TimeSpan.FromMinutes(o.ChunkMinutes), cancellationToken);

        foreach (var chunk in chunks)
        {
            var segmentsPath = Path.ChangeExtension(chunk.Path, ".json");
            if (File.Exists(segmentsPath))
            {
                continue;
            }

            TranscriptionResult result;
            await using (var audio = File.OpenRead(chunk.Path))
            {
                result = await transcription.TranscribeAsync(audio, Path.GetFileName(chunk.Path), cancellationToken);
            }

            var shifted = result.Segments.Select(s => s with { Start = s.Start + chunk.Offset, End = s.End + chunk.Offset }).ToList();
            await File.WriteAllTextAsync(segmentsPath + ".part", JsonSerializer.Serialize(shifted, JsonOptions), cancellationToken);
            File.Move(segmentsPath + ".part", segmentsPath, overwrite: true);
            LogChunk(episode.Title, chunk.Index + 1, chunks.Count);
        }

        var segments = new List<TranscriptSegment>();
        foreach (var chunk in chunks)
        {
            var json = await File.ReadAllTextAsync(Path.ChangeExtension(chunk.Path, ".json"), cancellationToken);
            segments.AddRange(JsonSerializer.Deserialize<List<TranscriptSegment>>(json, JsonOptions) ?? []);
        }

        using (await stateLock.AcquireAsync(cancellationToken))
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var tracked = await db.Episodes.Include(e => e.Transcript).SingleOrDefaultAsync(e => e.Id == episode.Id, cancellationToken);
            if (tracked is null || tracked.State != EpisodeState.Transcribing)
            {
                // Reset or deleted while we worked; its work folder is cleaned up as an orphan.
                return WorkOutcome.Worked;
            }

            var now = time.GetUtcNow().UtcDateTime;
            if (tracked.Transcript is not null)
            {
                db.Transcripts.Remove(tracked.Transcript);
            }

            db.Transcripts.Add(new Transcript
            {
                EpisodeId = tracked.Id,
                Source = TranscriptSource.Transcribed,
                Format = TranscriptFormat.SpeechToText,
                Provider = transcription.Provider,
                Model = transcription.Model,
                CreatedAt = now,
                Segments = segments,
            });
            tracked.State = EpisodeState.Transcribed;
            tracked.Attempts = 0;
            tracked.NextAttemptAt = null;
            tracked.LastError = null;
            tracked.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        workspace.Delete(episode.Id);
        LogTranscribed(episode.Title, segments.Count, chunks.Count);
        return WorkOutcome.Worked;
    }

    /// <summary>Streams the enclosure to disk through the guarded client. Error messages never include the URL, which can carry a token.</summary>
    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DownloadTimeout);
        var part = destination + ".part";
        try
        {
            using var response = await httpFactory.CreateClient(ServiceCollectionExtensions.EnclosureHttpClient)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone)
            {
                throw new AudioDownloadException($"Audio download refused (HTTP {(int)response.StatusCode}); the link may have expired.", linkMayHaveExpired: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AudioDownloadException($"Audio download failed (HTTP {(int)response.StatusCode}).");
            }

            if (response.Content.Headers.ContentLength > MaxAudioBytes)
            {
                throw new AudioDownloadException("Audio file is larger than 1.5 GB.");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var output = File.Create(part))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cts.Token)) > 0)
                {
                    total += read;
                    if (total > MaxAudioBytes)
                    {
                        throw new AudioDownloadException("Audio file is larger than 1.5 GB.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                }
            }

            File.Move(part, destination, overwrite: true);
        }
        catch (HttpRequestException ex)
        {
            throw new AudioDownloadException($"Audio download failed: {ex.Message}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AudioDownloadException("Audio download timed out.");
        }
        finally
        {
            File.Delete(part);
        }
    }

    private void EnsureFreeSpace()
    {
        var required = (long)options.Value.MinFreeDiskGb * 1024 * 1024 * 1024;
        if (workspace.FreeBytes() < required)
        {
            throw new InsufficientDiskSpaceException($"Less than {options.Value.MinFreeDiskGb} GB free on the data volume; transcription is paused.");
        }
    }

    /// <summary>Counts a failed attempt. The episode stays claimed with a back-off, or fails for good after the last attempt.</summary>
    private async Task RecordFailureAsync(Episode episode, string error, DateTime? notBefore, CancellationToken cancellationToken)
    {
        using var _ = await stateLock.AcquireAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tracked = await db.Episodes.SingleOrDefaultAsync(e => e.Id == episode.Id, cancellationToken);
        if (tracked is null || tracked.State != EpisodeState.Transcribing)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        tracked.Attempts++;
        tracked.UpdatedAt = now;
        if (tracked.Attempts >= polling.Value.MaxAttempts)
        {
            tracked.State = EpisodeState.Failed;
            tracked.NextAttemptAt = null;
            tracked.PrioritisedAt = null;
            tracked.LastError = $"Gave up after {tracked.Attempts} attempts: {error}";
            workspace.Delete(tracked.Id);
        }
        else
        {
            var backoff = now + TranscriptFetcher.Backoff(tracked.Attempts);
            tracked.NextAttemptAt = notBefore > backoff ? notBefore : backoff;
            tracked.LastError = error;
        }

        await db.SaveChangesAsync(cancellationToken);
        LogFailed(episode.Title, tracked.Attempts, error);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcribed chunk {Chunk} of {Count} for {Episode}")]
    private partial void LogChunk(string episode, int chunk, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcribed {Episode}: {Segments} segments from {Chunks} chunks")]
    private partial void LogTranscribed(string episode, int segments, int chunks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcription failed for {Episode} (attempt {Attempt}): {Error}")]
    private partial void LogFailed(string episode, int attempt, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcription paused: {Reason}")]
    private partial void LogPaused(string reason);
}

/// <summary>The episode's audio couldn't be downloaded. Counts against the episode.</summary>
public sealed class AudioDownloadException(string message, bool linkMayHaveExpired = false) : Exception(message)
{
    /// <summary>The host refused with 403 or 410, which for signed links usually means the URL expired.</summary>
    public bool LinkMayHaveExpired { get; } = linkMayHaveExpired;
}

/// <summary>Not enough free disk to download or split audio. Pauses transcription without counting against the episode.</summary>
public sealed class InsufficientDiskSpaceException(string message) : Exception(message);
