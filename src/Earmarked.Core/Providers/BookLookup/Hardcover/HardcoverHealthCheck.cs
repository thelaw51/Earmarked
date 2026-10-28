using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Earmarked.Core.Backends;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Configuration;

namespace Earmarked.Core.Providers.BookLookup.Hardcover;

/// <summary>
/// Reports whether Hardcover answers and accepts the token, by sending a one-result search for a fixed word and
/// parsing the answer the way a real search would: the shared <see cref="CatalogHealthCheck"/>'s plain GET wouldn't
/// show a rejected token, and a GraphQL error comes back as HTTP 200. Every request counts against Hardcover's daily
/// limit, so an answer is reused: a working search or a token problem for <see cref="RecheckAfter"/> (about 48
/// requests a day), "slow down" or a server error for <see cref="RecheckOutageAfter"/>, so the row clears soon after
/// an outage. Only "can't connect", which costs nothing, is checked again each time. Sends no mention data.
/// </summary>
public sealed class HardcoverHealthCheck(IHttpClientFactory httpFactory, IOptions<HardcoverOptions> options, TimeProvider time) : IHealthCheck
{
    internal static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan RecheckOutageAfter = TimeSpan.FromMinutes(5);

    /// <summary>A reference, so a caller reads the time and the result of one check, never one of each.</summary>
    private Cached? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_last is { } last && time.GetUtcNow() < last.Until)
        {
            return last.Result;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(BackendHealth.Timeout);
        HttpStatusCode status;
        string? body = null;
        try
        {
            using var request = HardcoverCatalog.SearchRequest("book", 1, options.Value.Token);
            using var response = await httpFactory.CreateClient(HardcoverCatalog.HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            status = response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(CatalogHttp.MaxResponseBytes, cts.Token);
                body = await response.Content.ReadAsStringAsync(cts.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _last = null;
            return HealthCheckResult.Unhealthy(ex is OperationCanceledException ? "Unreachable: no answer within 5 seconds." : $"Unreachable: {ex.Message}");
        }

        var outage = CatalogHttp.IsUnavailable(status);
        var result = status switch
        {
            HttpStatusCode.Unauthorized => HealthCheckResult.Unhealthy("Token rejected (HTTP 401). It may have expired: make a new one and set Hardcover:Token."),
            HttpStatusCode.Forbidden => HealthCheckResult.Unhealthy("Token refused (HTTP 403). It needs the read:catalog scope."),
            _ when outage => HealthCheckResult.Unhealthy($"Reachable, but answering HTTP {(int)status}."),
            _ when body is null => HealthCheckResult.Unhealthy($"Answering HTTP {(int)status}."),
            _ => ParsedOrError(body),
        };
        _last = new Cached(time.GetUtcNow() + (outage ? RecheckOutageAfter : RecheckAfter), result);
        return result;
    }

    private static HealthCheckResult ParsedOrError(string body)
    {
        try
        {
            HardcoverCatalog.Parse(body);
            return HealthCheckResult.Healthy(BookLookupProviders.Label(BookLookupProviders.Hardcover));
        }
        catch (CatalogLookupFailedException ex)
        {
            // The message is the adapter's own fixed text, never Hardcover's.
            return HealthCheckResult.Unhealthy($"Answers, but a search fails: {ex.Message}");
        }
    }

    private sealed record Cached(DateTimeOffset Until, HealthCheckResult Result);
}
