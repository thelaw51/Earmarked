namespace PodcastBookRecs.Core.Backends;

/// <summary>
/// Checks whether an HTTP backend answers at all. Used by health checks, and by adapters to tell "the backend went
/// away mid-request" (pause) from "it crashed on this input and was restarted" (count the attempt).
/// </summary>
public static class BackendProbe
{
    /// <summary>
    /// How long a backend that dropped a request has to answer again before it counts as gone. launchd waits 10
    /// seconds before restarting a crashed server on the Mac, and loading a model takes a few more.
    /// </summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends a GET to <paramref name="path"/> (the base URL by default). Returns null if anything answers, or why it
    /// didn't. OpenAI-compatible APIs have no standard health endpoint, so any HTTP response counts as reachable.
    /// </summary>
    public static async Task<string?> ProbeAsync(HttpClient client, CancellationToken cancellationToken, string path = "")
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(BackendHealth.Timeout);
        try
        {
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return ex is OperationCanceledException ? "no answer within 5 seconds." : ex.Message;
        }
    }

    /// <summary>Asks the backend again every 2 seconds until it answers or <paramref name="window"/> runs out.</summary>
    public static async Task<bool> AnswersWithinAsync(HttpClient client, TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (await ProbeAsync(client, cancellationToken) is null)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }
}
