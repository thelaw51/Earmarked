namespace PodcastBookRecs.Core.Backends;

/// <summary>Names shared by every backend health check and the status publisher.</summary>
public static class BackendHealth
{
    /// <summary>Health-check tag for external backends. Tagged checks feed the Status page but not the container's /health.</summary>
    public const string Tag = "backend";

    /// <summary>How often backends are checked, and each check's own time limit.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
}
