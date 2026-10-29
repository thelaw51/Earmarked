using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Earmarked.Core.Backends;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Notifications;

namespace Earmarked.Core.Providers.Notifications.Ntfy;

/// <summary>
/// Publishes to an ntfy server as JSON at its root URL, with the topic in the body. JSON rather than ntfy's
/// <c>Title:</c> and <c>Tags:</c> headers, because titles come from feeds and the model, and headers can't carry
/// non-ASCII text or line breaks safely. Markdown and action buttons stay off: the text is untrusted.
/// One instance serves the dispatcher and the health check, so the Status row can say when ntfy refuses messages.
/// </summary>
public sealed class NtfyNotificationSender(IHttpClientFactory httpFactory, IOptions<NotificationsOptions> options) : INotificationSender
{
    public const string HttpClientName = "ntfy";

    /// <summary>The HTTP status of the last message ntfy refused, or 0 once one is accepted again.</summary>
    public int RejectedStatus { get; private set; }

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "")
        {
            Content = new StringContent(Body(message, options.Value.Topic!).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = Authorization(options.Value);

        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new BackendUnavailableException($"Can't reach ntfy: {ex.HttpRequestError}.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BackendUnavailableException("ntfy didn't answer in time.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                RejectedStatus = 0;
                return;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500)
            {
                throw new BackendUnavailableException($"ntfy answered HTTP {status}.");
            }

            // 401/403: wrong token or no write access to the topic. 413: too long. The body isn't kept: it can echo the request.
            RejectedStatus = status;
            throw new NotificationRejectedException($"ntfy refused the message with HTTP {status}.");
        }
    }

    internal static JsonObject Body(NotificationMessage message, string topic)
    {
        var body = new JsonObject
        {
            ["topic"] = topic,
            ["title"] = message.Title,
            ["message"] = message.Body,
            ["priority"] = Priority(message.Kind),
            ["tags"] = new JsonArray(Tag(message.Kind)),
        };
        if (message.ClickUrl is not null)
        {
            body["click"] = message.ClickUrl;
        }

        return body;
    }

    /// <summary>A bearer token when one is set, otherwise basic auth when a user is, otherwise none.</summary>
    internal static AuthenticationHeaderValue? Authorization(NotificationsOptions o) =>
        !string.IsNullOrEmpty(o.Token) ? new AuthenticationHeaderValue("Bearer", o.Token)
        : !string.IsNullOrEmpty(o.Username) ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.Username}:{o.Password}")))
        : null;

    /// <summary>ntfy's 1 (min) to 5 (max). Outages are high so they make a sound; the summary is low so it doesn't.</summary>
    private static int Priority(NotificationKind kind) => kind switch
    {
        NotificationKind.BackendDown => 4,
        NotificationKind.EpisodeFailed => 4,
        NotificationKind.DailySummary => 2,
        _ => 3,
    };

    /// <summary>ntfy shows tags that name an emoji as that emoji in front of the title.</summary>
    private static string Tag(NotificationKind kind) => kind switch
    {
        NotificationKind.EpisodeExtracted => "books",
        NotificationKind.EpisodeFailed => "warning",
        NotificationKind.BackendDown => "rotating_light",
        NotificationKind.BackendRecovered => "white_check_mark",
        NotificationKind.DailySummary => "bar_chart",
        _ => "bell",
    };
}

/// <summary>
/// The "notifications" Status row: whether ntfy answers on <c>/v1/health</c>, and whether it refused the last message.
/// A reachable server can still reject every message (an expired token), which only the sender sees.
/// </summary>
public sealed class NtfyHealthCheck(IHttpClientFactory httpFactory, NtfyNotificationSender sender) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problem = await BackendProbe.ProbeAsync(httpFactory.CreateClient(NtfyNotificationSender.HttpClientName), cancellationToken, "v1/health");
        if (problem is not null)
        {
            return HealthCheckResult.Unhealthy($"Unreachable: {problem}");
        }

        return sender.RejectedStatus is var status and > 0
            ? HealthCheckResult.Unhealthy($"Answers, but refused the last message (HTTP {status}). Check the topic and the token's write access.")
            : HealthCheckResult.Healthy(NotificationProviders.Ntfy);
    }
}
