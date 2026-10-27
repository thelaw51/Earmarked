namespace PodcastBookRecs.Core.Backends;

/// <summary>Names shared by every backend health check and the status publisher.</summary>
public static class BackendHealth
{
    /// <summary>Health-check tag for external backends. Tagged checks feed the Status page but not the container's /health.</summary>
    public const string Tag = "backend";

    /// <summary>
    /// The book lookup check's name, which is also its Status row. The row exists only while lookup is configured, so
    /// the UI, which can't read the Worker's config, uses it to tell "waiting for lookup" from "lookup is off".
    /// </summary>
    public const string BookLookup = "book lookup";

    /// <summary>
    /// The book extraction check's name and Status row. Like <see cref="BookLookup"/>, it exists only while extraction
    /// is configured, which tells the UI whether an episode with a transcript has anything left to do.
    /// </summary>
    public const string Extraction = "extraction";

    /// <summary>How often backends are checked, and each check's own time limit.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
}
