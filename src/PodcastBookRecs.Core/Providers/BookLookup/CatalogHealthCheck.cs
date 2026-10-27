using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PodcastBookRecs.Core.Backends;

namespace PodcastBookRecs.Core.Providers.BookLookup;

/// <summary>
/// Reports whether a catalogue answers and isn't asking us to slow down, by requesting its base address through the
/// adapter's own HTTP client. A healthy answer is reused for 5 minutes, so the Status page's 30-second schedule doesn't
/// become a steady stream of requests; a problem is checked again every time, so recovery shows straight away. Sends no
/// mention data. Register one instance per catalogue as a singleton, so its memory of the last answer survives.
/// </summary>
public sealed class CatalogHealthCheck(IHttpClientFactory httpFactory, TimeProvider time, string httpClientName, string label) : IHealthCheck
{
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _healthyAt;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_healthyAt is { } at && time.GetUtcNow() - at < RecheckAfter)
        {
            return HealthCheckResult.Healthy(label);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(BackendHealth.Timeout);
        try
        {
            using var response = await httpFactory.CreateClient(httpClientName).GetAsync("", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                _healthyAt = null;
                return HealthCheckResult.Unhealthy($"Reachable, but answering HTTP {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _healthyAt = null;
            return HealthCheckResult.Unhealthy(ex is OperationCanceledException ? "Unreachable: no answer within 5 seconds." : $"Unreachable: {ex.Message}");
        }

        _healthyAt = time.GetUtcNow();
        return HealthCheckResult.Healthy(label);
    }
}
