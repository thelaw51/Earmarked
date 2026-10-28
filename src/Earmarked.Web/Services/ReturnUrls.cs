namespace Earmarked.Web.Services;

/// <summary>Where to send someone after sign-in.</summary>
internal static class ReturnUrls
{
    /// <summary>
    /// Only follows return URLs on this site, so a crafted link can't send someone elsewhere after sign-in. Control
    /// characters are refused too: browsers strip tabs and newlines, which turns <c>/\t/evil.com</c> into <c>//evil.com</c>.
    /// </summary>
    public static string LocalOrHome(string? url) =>
        url is ['/'] or ['/', not ('/' or '\\'), ..] && !url.Any(char.IsControl) ? url : "/";
}
