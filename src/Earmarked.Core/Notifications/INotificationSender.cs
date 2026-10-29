using Earmarked.Core.Data;

namespace Earmarked.Core.Notifications;

/// <summary>
/// Delivers one push notification. Adapters live under <c>Providers/Notifications/</c>; only
/// <see cref="NotificationDispatcher"/> calls this, so a slow or missing notification server never holds up the pipeline.
/// </summary>
public interface INotificationSender
{
    /// <summary>
    /// Sends <paramref name="message"/>. Throws <see cref="Backends.BackendUnavailableException"/> when the server can't
    /// be reached or asks us to slow down, and <see cref="NotificationRejectedException"/> when it refuses this message.
    /// </summary>
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// A notification in our own terms. Adapters choose the provider's priority and icon from <see cref="Kind"/>.
/// <paramref name="ClickUrl"/> is built from <see cref="Configuration.NotificationsOptions.AppUrl"/> and our own IDs, never
/// from feed or model text.
/// </summary>
public sealed record NotificationMessage(NotificationKind Kind, string Title, string Body, string? ClickUrl);

/// <summary>The notification server answered but refused the message, e.g. a rejected token or unknown topic.</summary>
public sealed class NotificationRejectedException(string message) : Exception(message);
