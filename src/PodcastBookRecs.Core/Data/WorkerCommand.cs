namespace PodcastBookRecs.Core.Data;

/// <summary>A request from the UI for the Worker to do something now. The Worker polls this table.</summary>
public class WorkerCommand
{
    public int Id { get; set; }
    public WorkerCommandType Type { get; set; }
    public int? PodcastId { get; set; }
    public int? EpisodeId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public enum WorkerCommandType
{
    PollFeed = 0,
    PollAllFeeds = 1,
    RetryEpisode = 2,
}
