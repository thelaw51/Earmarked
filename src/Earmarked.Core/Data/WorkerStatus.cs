namespace Earmarked.Core.Data;

/// <summary>Heartbeat for a Worker component or backend, shown on the UI status page.</summary>
public class WorkerStatus
{
    /// <summary>Component name, e.g. <c>worker</c>.</summary>
    public required string Name { get; set; }

    public bool Healthy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Detail { get; set; }
    public string? Version { get; set; }
}

/// <summary>Well-known <see cref="WorkerStatus.Name"/> values.</summary>
public static class WorkerStatusNames
{
    public const string Worker = "worker";

    /// <summary>How long without a heartbeat before the UI shows the Worker as down.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);
}
