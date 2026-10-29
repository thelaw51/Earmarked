using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Earmarked.Core.Books;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;

namespace Earmarked.Core.Notifications;

/// <summary>
/// Adds one summary notification a day, at <see cref="NotificationsOptions.DailySummaryTime"/> in the container's
/// time zone, or as soon as the Worker is running after that time. It counts from the previous summary (or the last
/// 24 hours, for the first one or after a gap of more than two days) to now, so nothing falls between two summaries
/// or lands in both. A summary goes out even when nothing happened, which also says the Worker is alive.
/// </summary>
public sealed partial class DailySummary(
    IDbContextFactory<AppDbContext> dbFactory,
    NotificationOutbox outbox,
    IOptions<NotificationsOptions> options,
    TimeProvider time,
    ILogger<DailySummary> logger)
{
    private static readonly TimeSpan LongestWindow = TimeSpan.FromHours(48);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.DailySummary)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, time.LocalTimeZone);
        if (TimeOnly.FromDateTime(local) < o.SummaryTime)
        {
            return;
        }

        var key = $"daily-summary:{local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Notifications.AnyAsync(n => n.DedupeKey == key, cancellationToken))
        {
            return;
        }

        var previous = await db.Notifications
            .Where(n => n.Kind == NotificationKind.DailySummary)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => (DateTime?)n.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var from = previous is { } p && now - p <= LongestWindow ? p : now.AddHours(-24);

        var counts = await CountAsync(db, from, now, cancellationToken);
        var (title, body) = Format(counts);
        outbox.DailySummary(db, title, body, key);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Most likely the unique key, if another save got there first; otherwise the next minute tries again.
            LogNotSaved(ex);
            return;
        }

        LogQueued(counts.Episodes, counts.Mentions);
    }

    internal static async Task<SummaryCounts> CountAsync(AppDbContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        // Episodes have no "extracted at"; an extracted episode's UpdatedAt is when it finished, unless it was re-run since.
        var episodes = await db.Episodes.CountAsync(e => e.State == EpisodeState.Extracted && e.UpdatedAt >= from && e.UpdatedAt < to, cancellationToken);
        var mentions = await db.BookMentions.CountAsync(m => m.CreatedAt >= from && m.CreatedAt < to, cancellationToken);
        var matched = await db.BookMentions
            .Where(m => m.CreatedAt >= from && m.CreatedAt < to && m.MatchState == MatchState.Matched && m.BookId != null)
            .Select(m => m.BookId)
            .Distinct()
            .CountAsync(cancellationToken);
        var transcribed = await db.Transcripts.CountAsync(t => t.CreatedAt >= from && t.CreatedAt < to && t.Source == TranscriptSource.Transcribed, cancellationToken);
        var fetched = await db.Transcripts.CountAsync(t => t.CreatedAt >= from && t.CreatedAt < to && t.Source == TranscriptSource.Feed, cancellationToken);
        var failed = await db.Episodes.CountAsync(
            e => (e.State == EpisodeState.Failed || e.State == EpisodeState.ExtractionFailed) && e.UpdatedAt >= from && e.UpdatedAt < to, cancellationToken);
        var review = await ReviewBacklog.CountAsync(db, cancellationToken);
        return new SummaryCounts(episodes, mentions, matched, transcribed, fetched, failed, review);
    }

    internal static (string Title, string Body) Format(SummaryCounts c)
    {
        var title = $"Today: {Plural(c.Mentions, "book")} from {Plural(c.Episodes, "episode")}";
        var lines = new List<string>
        {
            $"Episodes searched for books: {c.Episodes}",
            $"Books found: {c.Mentions} ({c.Matched} matched to a catalogue so far)",
            $"Transcripts: {c.Transcribed} transcribed, {c.Fetched} published by the podcast",
        };
        if (c.Failed > 0)
        {
            lines.Add($"Episodes that gave up: {c.Failed}");
        }

        if (c.WaitingForReview > 0)
        {
            lines.Add($"Waiting for review: {c.WaitingForReview}");
        }

        return (title, string.Join('\n', lines));
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save the daily summary")]
    private partial void LogNotSaved(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Queued the daily summary: {Episodes} episode(s), {Mentions} book mention(s)")]
    private partial void LogQueued(int episodes, int mentions);
}

/// <summary>What one daily summary reports.</summary>
internal sealed record SummaryCounts(int Episodes, int Mentions, int Matched, int Transcribed, int Fetched, int Failed, int WaitingForReview);
