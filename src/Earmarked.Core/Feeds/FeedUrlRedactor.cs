namespace Earmarked.Core.Feeds;

/// <summary>
/// Private feeds carry a subscriber token in the path or query, so logs and the UI only ever show the host.
/// </summary>
public static class FeedUrlRedactor
{
    public static string Redact(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.PathAndQuery is "/" or "" ? $"{uri.Scheme}://{uri.Host}/" : $"{uri.Scheme}://{uri.Host}/…"
            : "(invalid URL)";
}
