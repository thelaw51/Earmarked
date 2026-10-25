using System.ComponentModel.DataAnnotations;

namespace PodcastBookRecs.Core.Configuration;

/// <summary>Where state lives. <c>/data</c> must be local disk, not a network share: SQLite locking breaks over SMB.</summary>
public sealed class PathsOptions
{
    public const string Section = "Paths";

    [Required]
    public string Data { get; set; } = "/data";

    public string DatabaseFile => Path.Combine(Data, "podcastbookrecs.db");
    public string KeysDirectory => Path.Combine(Data, "keys");
}

/// <summary>Feed polling and job retry settings.</summary>
public sealed class PollingOptions
{
    public const string Section = "Polling";

    [Range(5, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>How many of the newest episodes to process when a feed is first added. Older ones are stored as Skipped.</summary>
    [Range(0, 1000)]
    public int BackfillEpisodes { get; set; } = 10;

    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>A feed listed in config.json, imported into the database on Worker startup if not already present.</summary>
public sealed class FeedSeed
{
    [Required]
    public string Url { get; set; } = "";
}

/// <summary>Outbound HTTP settings for feed-supplied URLs.</summary>
public sealed class HttpOptions
{
    public const string Section = "Http";

    /// <summary>
    /// Lets feeds, transcripts and audio be fetched from private, loopback and link-local addresses. Off by default:
    /// feed content is untrusted, and without this guard a feed could point the Worker at devices on your network.
    /// </summary>
    public bool AllowPrivateNetworkFeeds { get; set; }
}
