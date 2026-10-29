namespace Earmarked.Core.Data;

/// <summary>
/// A push notification waiting to be sent, or one already sent. The pipeline adds rows in the same save as the change
/// they report, and <c>NotificationDispatcher</c> delivers them, so a notification server that's down delays messages
/// instead of losing them or slowing the pipeline.
/// </summary>
public class Notification
{
    public const int MaxTitleLength = 250;
    public const int MaxBodyLength = 1000;
    public const int MaxUrlLength = 500;
    public const int MaxDedupeKeyLength = 100;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public NotificationKind Kind { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? ClickUrl { get; set; }

    /// <summary>Unique when set, so a message that must go once (one daily summary per day) can't be added twice.</summary>
    public string? DedupeKey { get; set; }

    public NotificationState State { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

/// <summary>What a notification reports. Values are stored, so never renumber them.</summary>
public enum NotificationKind
{
    /// <summary>An episode's transcript has been searched for books.</summary>
    EpisodeExtracted = 0,

    /// <summary>An episode ran out of transcription or extraction attempts.</summary>
    EpisodeFailed = 1,

    /// <summary>A backend's Status row has been unhealthy for a while.</summary>
    BackendDown = 2,

    /// <summary>A backend that was reported down is healthy again.</summary>
    BackendRecovered = 3,

    /// <summary>The once-a-day count of what the pipeline did.</summary>
    DailySummary = 4,
}

/// <summary>Where a notification is in delivery. Values are stored, so never renumber them.</summary>
public enum NotificationState
{
    Pending = 0,
    Sent = 1,

    /// <summary>Refused too often, or too old to be worth sending once the server came back.</summary>
    Dropped = 2,
}
