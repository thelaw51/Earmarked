using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Providers.BookLookup.OpenLibrary;

namespace PodcastBookRecs.Tests;

public class OpenLibraryCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Maps_a_recorded_search_to_candidates_and_skips_unusable_entries()
    {
        var books = OpenLibraryCatalog.Parse(Fixtures.Read("openlibrary-search.json"));

        Assert.Equal(["OL1001W", "OL1002W", "OL1004W"], books.Select(b => b.CatalogId));
        Assert.Equal(
            new CatalogBook("OL1001W", "The Lighthouse Keeper's Almanac", null, ["Ines Calder"], 2019, 4,
                "https://openlibrary.org/works/OL1001W", "https://covers.openlibrary.org/b/id/12345-M.jpg"),
            books[0],
            new CandidateComparer());
        Assert.Equal(["Tomas Reyne"], books[2].Authors);
        Assert.Equal("A Walking Year", books[2].Subtitle);
        Assert.Null(books[1].CoverUrl);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"numFound":0}""")]
    public void Rejects_a_body_that_is_not_a_search_result(string body) =>
        Assert.Throws<CatalogLookupFailedException>(() => OpenLibraryCatalog.Parse(body));

    [Fact]
    public async Task Searches_the_title_and_author_fields_escaped()
    {
        var handler = new Recorder(HttpStatusCode.OK, Fixtures.Read("openlibrary-search.json"));

        await Catalog(handler).SearchAsync("Rivers & Roads?", "Ines Calder", Ct);

        var query = handler.Uris.Single().Query;
        Assert.Contains("title=Rivers%20%26%20Roads%3F", query, StringComparison.Ordinal);
        Assert.Contains("author=Ines%20Calder", query, StringComparison.Ordinal);
        Assert.DoesNotContain("q", query.TrimStart('?').Split('&').Select(p => p.Split('=')[0]));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Slow_down_and_server_errors_pause(HttpStatusCode status) =>
        await Assert.ThrowsAsync<BackendUnavailableException>(() => Catalog(new Recorder(status, "")).SearchAsync("A Title", null, Ct));

    [Fact]
    public async Task Other_errors_fail_only_this_title() =>
        await Assert.ThrowsAsync<CatalogLookupFailedException>(() => Catalog(new Recorder(HttpStatusCode.BadRequest, "")).SearchAsync("A Title", null, Ct));

    [Fact]
    public async Task An_oversized_answer_fails_only_this_title()
    {
        var huge = new string(' ', 2 * 1024 * 1024);

        await Assert.ThrowsAsync<CatalogLookupFailedException>(() => Catalog(new Recorder(HttpStatusCode.OK, huge)).SearchAsync("A Title", null, Ct));
    }

    [Fact]
    public async Task Spaces_requests_apart()
    {
        var catalog = Catalog(new Recorder(HttpStatusCode.OK, """{"docs":[]}"""));
        catalog.RequestGap = TimeSpan.FromMilliseconds(300);

        var watch = Stopwatch.StartNew();
        await catalog.SearchAsync("One", null, Ct);
        await catalog.SearchAsync("Two", null, Ct);

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(250), $"Took {watch.Elapsed}");
    }

    [Fact]
    public async Task Sends_the_contact_email_in_the_user_agent()
    {
        var handler = new Recorder(HttpStatusCode.OK, """{"docs":[]}""");
        using var provider = Services(new() { ["OpenLibrary:ContactEmail"] = "reader@example.com" }, handler);

        await provider.GetRequiredService<IBookCatalog>().SearchAsync("A Title", null, Ct);

        Assert.EndsWith("; reader@example.com)", handler.UserAgents.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lookup_can_be_turned_off()
    {
        using var provider = Services(new() { ["BookLookup:Provider"] = "" }, new Recorder(HttpStatusCode.OK, ""));

        Assert.Null(provider.GetService<IBookCatalog>());
    }

    [Fact]
    public async Task Health_check_asks_at_most_every_five_minutes()
    {
        var handler = new Recorder(HttpStatusCode.OK, "");
        using var provider = Services([], handler);
        var check = provider.GetRequiredService<OpenLibraryHealthCheck>();

        var first = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        var second = await provider.GetRequiredService<OpenLibraryHealthCheck>().CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Healthy, first.Status);
        Assert.Equal(HealthStatus.Healthy, second.Status);
        Assert.Single(handler.Uris);
    }

    private static OpenLibraryCatalog Catalog(HttpMessageHandler handler) =>
        new(new SingleClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://catalog.test/") }), TimeProvider.System) { RequestGap = TimeSpan.Zero };

    /// <summary>The real registration, with the network swapped for <paramref name="handler"/>.</summary>
    private static ServiceProvider Services(Dictionary<string, string?> settings, HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddPbrCore(config).AddPbrBookLookup(config);
        services.AddHttpClient(OpenLibraryCatalog.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>Records compare lists by reference; this compares the authors' contents.</summary>
    private sealed class CandidateComparer : IEqualityComparer<CatalogBook>
    {
        public bool Equals(CatalogBook? x, CatalogBook? y) =>
            x is not null && y is not null && x with { Authors = [] } == y with { Authors = [] } && x.Authors.SequenceEqual(y.Authors);

        public int GetHashCode(CatalogBook obj) => obj.CatalogId.GetHashCode(StringComparison.Ordinal);
    }
}
