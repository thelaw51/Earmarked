namespace Earmarked.Core.Feeds;

/// <summary>Validation for feed URLs entered in the UI or config.</summary>
public static class FeedUrl
{
    /// <summary>Only absolute http and https URLs. Anything else (file:, ftp:) could make the Worker read local resources.</summary>
    public static bool IsValid(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
