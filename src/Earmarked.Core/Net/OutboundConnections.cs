using System.Net;
using System.Net.Sockets;

namespace Earmarked.Core.Net;

/// <summary>
/// Connection callbacks for <see cref="SocketsHttpHandler"/>. Exceptions thrown here reach callers as
/// <see cref="HttpRequestException"/> with <see cref="HttpRequestError.ConnectionError"/>, and the handler appends only
/// <c>(host:port)</c> to the message, never the URL path, so tokens in private feed URLs can't leak through it.
/// </summary>
public static class OutboundConnections
{
    /// <summary>
    /// For feed-supplied URLs. Resolves the host once, refuses if any address is private, loopback, link-local or
    /// otherwise not public, then connects to the vetted address itself, so a second DNS answer can't swap in a
    /// private one. Runs for every connection, redirects included. Pair with <c>UseProxy = false</c>: behind a proxy
    /// this callback would vet the proxy, not the target.
    /// </summary>
    public static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (addresses.Length == 0 || addresses.Any(PrivateAddressGuard.IsBlocked))
        {
            throw new BlockedAddressException();
        }

        return await ConnectAsync(addresses, context.DnsEndPoint.Port, timeout: null, cancellationToken);
    }

    /// <summary>
    /// For user-configured backends. Applies its own connect timeout so "can't reach the backend" always surfaces as
    /// <see cref="HttpRequestError.ConnectionError"/>. SocketsHttpHandler's own ConnectTimeout throws the same
    /// <see cref="TaskCanceledException"/> as a request timeout, which would make the two indistinguishable.
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> ConnectWithTimeout(TimeSpan timeout) =>
        async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host.Trim('[', ']');
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken);
            return await ConnectAsync(addresses, context.DnsEndPoint.Port, timeout, cancellationToken);
        };

    private static async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            cts.CancelAfter(timeout.Value);
        }

        try
        {
            await socket.ConnectAsync(addresses, port, cts.Token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException("Timed out connecting.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>Thrown when a feed-supplied URL resolves to an address the Worker must not contact.</summary>
public sealed class BlockedAddressException() : IOException("Blocked a request to a private network address.");

/// <summary>Decides which IP addresses feed-supplied URLs may reach.</summary>
public static class PrivateAddressGuard
{
    /// <summary>
    /// True for anything that isn't a public unicast address: loopback, private, link-local, carrier-grade NAT
    /// (which Tailscale uses), unique-local, unspecified, multicast, broadcast, reserved, and IPv6 forms that embed
    /// one of those IPv4 addresses (IPv4-mapped, IPv4-compatible, NAT64, 6to4).
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsBlockedV4(address.MapToIPv4());
            }

            var b = address.GetAddressBytes();
            if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6None.Equals(address)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            // IPv4-compatible ::a.b.c.d (first 96 bits zero) and NAT64 64:ff9b::a.b.c.d both carry an IPv4 address in the last 4 bytes.
            if (b.Take(12).All(x => x == 0) || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.Skip(4).Take(8).All(x => x == 0)))
            {
                return IsBlockedV4(new IPAddress(b[12..16]));
            }

            // 6to4 2002:a.b.c.d::/48 carries an IPv4 address in bytes 2-5.
            return b[0] == 0x20 && b[1] == 0x02 && IsBlockedV4(new IPAddress(b[2..6]));
        }

        return IsBlockedV4(address);
    }

    private static bool IsBlockedV4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            100 => b[1] >= 64 && b[1] <= 127,
            169 => b[1] == 254,
            172 => b[1] >= 16 && b[1] <= 31,
            192 => (b[1] == 168) || (b[1] == 0 && b[2] == 0),
            198 => b[1] == 18 || b[1] == 19,
            >= 224 => true,
            _ => false,
        };
    }
}
