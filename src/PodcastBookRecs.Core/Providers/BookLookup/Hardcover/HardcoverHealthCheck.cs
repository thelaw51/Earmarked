using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.Configuration;

namespace PodcastBookRecs.Core.Providers.BookLookup.Hardcover;

/// <summary>
/// Reports whether Hardcover answers and accepts the token, by sending a one-result search for a fixed word: the
/// shared <see cref="CatalogHealthCheck"/>'s plain GET wouldn't show a rejected token. Every request counts against
/// Hardcover's daily limit, so any answer it gives is reused for <see cref="RecheckAfter"/> (about 48 requests a
/// day); only "can't connect", which costs nothing, is checked again each time. Sends no mention data.
/// </summary>
public sealed class HardcoverHealthCheck(IHttpClientFactory httpFactory, IOptions<HardcoverOptions> options, TimeProvider time) : IHealthCheck
{
    internal static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(30);

    private (DateTimeOffset At, HealthCheckResult Result)? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_last is { } last && time.GetUtcNow() - last.At < RecheckAfter)
        {
            return last.Result;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(BackendHealth.Timeout);
        HttpStatusCode status;
        try
        {
            using var request = HardcoverCatalog.SearchRequest("book", 1, options.Value.Token);
            using var response = await httpFactory.CreateClient(HardcoverCatalog.HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            status = response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _last = null;
            return HealthCheckResult.Unhealthy(ex is OperationCanceledException ? "Unreachable: no answer within 5 seconds." : $"Unreachable: {ex.Message}");
        }

        var result = status switch
        {
            HttpStatusCode.Unauthorized => HealthCheckResult.Unhealthy("Token rejected (HTTP 401). It may have expired: make a new one and set Hardcover:Token."),
            HttpStatusCode.Forbidden => HealthCheckResult.Unhealthy("Token refused (HTTP 403). It needs the read:catalog scope."),
            _ when CatalogHttp.IsUnavailable(status) => HealthCheckResult.Unhealthy($"Reachable, but answering HTTP {(int)status}."),
            _ when (int)status is < 200 or > 299 => HealthCheckResult.Unhealthy($"Answering HTTP {(int)status}."),
            _ => HealthCheckResult.Healthy(BookLookupProviders.Label(BookLookupProviders.Hardcover)),
        };
        _last = (time.GetUtcNow(), result);
        return result;
    }
}
