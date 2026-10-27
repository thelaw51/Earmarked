namespace PodcastBookRecs.Web.Services;

/// <summary>
/// Log messages for UI actions that fail. Pages show the person a fixed message and log the exception here, because
/// exception text from SQLite or EF can carry file paths. Razor components can't host <c>[LoggerMessage]</c> methods
/// (source generators don't see each other's output), so they live in this class.
/// </summary>
internal static partial class UiLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "UI action {Action} failed")]
    public static partial void ActionFailed(this ILogger logger, Exception ex, string action);
}
