using Microsoft.AspNetCore.HttpOverrides;

namespace PodcastBookRecs.Web.Services;

/// <summary>
/// Which hops may tell the app the original scheme and client address. cloudflared terminates TLS and talks plain HTTP
/// to the app, so without this the app thinks every request is HTTP: the auth cookie never gets the Secure flag and
/// redirects point at <c>http://</c>.
/// </summary>
internal static class ProxyNetworks
{
    public const string Section = "ForwardedHeaders:KnownNetworks";

    /// <summary>
    /// Private ranges cover cloudflared in a sibling container or elsewhere on the LAN. Loopback is always trusted.
    /// A LAN host could spoof the headers, but only for its own requests, and the app uses them for nothing else.
    /// </summary>
    private static readonly string[] Defaults = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];

    public static IServiceCollection AddPbrForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var networks = configuration.GetSection(Section).Get<string[]>() is { Length: > 0 } configured ? configured : Defaults;
        return services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Not X-Forwarded-Host: cloudflared passes the original Host header through, and trusting a forwarded host
            // would let any LAN client choose the host the app builds redirects for.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });
    }
}
