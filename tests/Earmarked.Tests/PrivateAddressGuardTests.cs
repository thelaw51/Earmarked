using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Earmarked.Core.Configuration;
using Earmarked.Core.Net;

namespace Earmarked.Tests;

public class PrivateAddressGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.104")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("192.0.0.8")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:c0a8:0001::1")]
    public void Blocks_addresses_that_are_not_public(string address) =>
        Assert.True(PrivateAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:1.1.1.1")]
    [InlineData("64:ff9b::101:101")]
    [InlineData("2002:0101:0101::1")]
    public void Allows_public_addresses(string address) =>
        Assert.False(PrivateAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://127.0.0.1:9/feed.xml")]
    [InlineData("http://localhost:9/feed.xml")]
    [InlineData("http://[::1]:9/feed.xml")]
    public async Task The_real_feed_client_refuses_private_addresses(string url)
    {
        using var provider = Services(allowPrivate: false);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceCollectionExtensions.FeedHttpClient);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url, TestContext.Current.CancellationToken));

        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
        Assert.IsType<BlockedAddressException>(ex.InnerException);
        Assert.DoesNotContain("feed.xml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_audio_client_is_guarded_too()
    {
        using var provider = Services(allowPrivate: false);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceCollectionExtensions.EnclosureHttpClient);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/episode.mp3", TestContext.Current.CancellationToken));

        Assert.IsType<BlockedAddressException>(ex.InnerException);
    }

    [Fact]
    public async Task The_escape_hatch_allows_private_addresses()
    {
        using var provider = Services(allowPrivate: true);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ServiceCollectionExtensions.FeedHttpClient);

        // Nothing listens on port 9, so the connection is refused rather than blocked.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/feed.xml", TestContext.Current.CancellationToken));

        Assert.IsNotType<BlockedAddressException>(ex.InnerException);
    }

    private static ServiceProvider Services(bool allowPrivate)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Http:AllowPrivateNetworkFeeds"] = allowPrivate.ToString() })
            .Build();
        return new ServiceCollection().AddLogging().AddEarmarkedCore(config).BuildServiceProvider();
    }
}
