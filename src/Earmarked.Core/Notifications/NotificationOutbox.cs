using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;

namespace Earmarked.Core.Notifications;

/// <summary>
/// Writes the notifications the pipeline sends, as <see cref="Notification"/> rows added to the caller's context, so
/// a notice is saved in the same <c>SaveChanges</c> as the change it reports and never goes out for a change that
/// was rolled back. Each method does nothing when its kind is switched off. Text never includes feed or audio URLs
/// or an episode's <c>LastError</c>, which can carry subscriber tokens.
/// </summary>
public sealed class NotificationOutbox(IOptions<NotificationsOptions> options, TimeProvider time)
{
    /// <summary>How many book titles an "extracted" notice lists before "and N more".</summary>
    public const int MaxListedTitles = 5;

    private const int MaxListedTitleLength = 100;

    /// <summary>Reports an episode whose transcript has been searched, listing the titles found, most confident first.</summary>
    public void EpisodeExtracted(AppDbContext db, Guid episodeId, string podcast, string episode, IReadOnlyList<string> titles)
    {
        var o = options.Value;
        if (!o.EpisodeExtracted || (titles.Count == 0 && !o.EpisodesWithoutBooks))
        {
            return;
        }

        var heading = titles.Count switch
        {
            0 => "No books found",
            1 => "1 book found",
            _ => $"{titles.Count} books found",
        };
        var body = new StringBuilder($"{episode} ({podcast})");
        foreach (var title in titles.Take(MaxListedTitles))
        {
            body.Append("\n- ").Append(Cap(Clean(title), MaxListedTitleLength));
        }

        if (titles.Count > MaxListedTitles)
        {
            body.Append(CultureInfo.InvariantCulture, $"\nand {titles.Count - MaxListedTitles} more");
        }

        Add(db, NotificationKind.EpisodeExtracted, heading, body.ToString(), EpisodeUrl(episodeId));
    }

    /// <summary>Reports an episode that ran out of attempts at <paramref name="stage"/> ("transcription" or "extraction").</summary>
    public void EpisodeFailed(AppDbContext db, Guid episodeId, string podcast, string episode, string stage, int attempts)
    {
        if (!options.Value.EpisodeFailures)
        {
            return;
        }

        Add(db, NotificationKind.EpisodeFailed, $"An episode's {stage} gave up",
            $"{episode} ({podcast}) failed {attempts} times. The episode page has the last error.", EpisodeUrl(episodeId));
    }

    /// <summary>Reports a backend that has been unhealthy for <see cref="NotificationsOptions.BackendAlertAfterMinutes"/>.</summary>
    public void BackendDown(AppDbContext db, string backend, string? detail) =>
        Add(db, NotificationKind.BackendDown, $"Problem with {backend}",
            string.IsNullOrWhiteSpace(detail) ? "Its health check is failing. The Status page has the details." : detail, PageUrl("status"));

    /// <summary>Reports a backend that was reported down and is healthy again.</summary>
    public void BackendRecovered(AppDbContext db, string backend, TimeSpan downFor) =>
        Add(db, NotificationKind.BackendRecovered, $"{Capitalise(backend)} is back",
            $"Healthy again after {Describe(downFor)}.", PageUrl("status"));

    /// <summary>The daily summary. <paramref name="dedupeKey"/> makes a second one for the same day fail to save.</summary>
    public void DailySummary(AppDbContext db, string title, string body, string dedupeKey) =>
        Add(db, NotificationKind.DailySummary, title, body, PageUrl("books"), dedupeKey);

    private void Add(AppDbContext db, NotificationKind kind, string title, string body, string? clickUrl, string? dedupeKey = null) =>
        db.Notifications.Add(new Notification
        {
            Kind = kind,
            Title = Cap(Clean(title), Notification.MaxTitleLength),
            Body = Cap(Clean(body), Notification.MaxBodyLength),
            ClickUrl = clickUrl,
            DedupeKey = dedupeKey,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        });

    private string? EpisodeUrl(Guid episodeId) => PageUrl($"episodes/{episodeId}");

    private string? PageUrl(string path) =>
        string.IsNullOrEmpty(options.Value.AppUrl) ? null : $"{options.Value.AppUrl.TrimEnd('/')}/{path}";

    /// <summary>"1 hour 5 minutes", "12 minutes" or "under a minute".</summary>
    internal static string Describe(TimeSpan span)
    {
        var minutes = (int)span.TotalMinutes;
        if (minutes < 1)
        {
            return "under a minute";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        var parts = new List<string>();
        if (hours > 0)
        {
            parts.Add(hours == 1 ? "1 hour" : $"{hours} hours");
        }

        if (rest > 0)
        {
            parts.Add(rest == 1 ? "1 minute" : $"{rest} minutes");
        }

        return string.Join(' ', parts);
    }

    private static string Capitalise(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>Control characters other than line breaks become spaces: titles come from feeds and the model.</summary>
    private static string Clean(string value) =>
        string.Create(value.Length, value, (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
            {
                span[i] = char.IsControl(s[i]) && s[i] != '\n' ? ' ' : s[i];
            }
        });

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..(max - 3)] + "...";
}
