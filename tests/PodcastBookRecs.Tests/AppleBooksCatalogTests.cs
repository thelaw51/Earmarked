using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Providers.BookLookup.AppleBooks;

namespace PodcastBookRecs.Tests;

/// <summary>The Apple Books adapter against an invented recorded search and a stub handler.</summary>
public sealed class AppleBooksCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Maps_a_recorded_search_to_candidates_and_skips_unusable_entries()
    {
        var books = AppleBooksCatalog.Parse(Fixtures.Read("applebooks-search.json"), "gb");

        Assert.Equal(["900000001", "900000002", "900000005", "900000006"], books.Select(b => b.CatalogId));
        var first = books[0];
        Assert.Equal(("Harbour Lights: A Novel", (int?)2025, (int?)null, BookLookupProviders.AppleBooks),
            (first.Title, first.FirstPublishYear, first.EditionCount, first.Provider));
        Assert.Equal(["Ines Calder", "Tomas Reyne"], first.Authors);
        Assert.Equal("https://books.apple.com/gb/book/id900000001", first.InfoUrl);
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/Publication/v4/aa/bb/cc/100x100bb.jpg", first.CoverUrl);
        Assert.Equal(["Ada Okafor", "Ines Calder", "Tomas Reyne"], books[1].Authors);
        Assert.Null(books[1].FirstPublishYear);
        Assert.All(books.Skip(1), b => Assert.Null(b.CoverUrl));
        Assert.All(books, b => Assert.True(CatalogMatcher.IsUsable(b)));
    }

    [Fact]
    public void Keeps_at_most_ten_candidates()
    {
        var results = string.Join(",", Enumerable.Range(1, 30).Select(i => $$"""{"trackId":{{i}},"trackName":"Book {{i}}"}"""));

        Assert.Equal(10, AppleBooksCatalog.Parse($$"""{"results":[{{results}}]}""", "us").Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"resultCount":0}""")]
    [InlineData("""{"results":{}}""")]
    public void Rejects_a_body_that_is_not_a_search_result(string body) =>
        Assert.Throws<CatalogLookupFailedException>(() => AppleBooksCatalog.Parse(body, "us"));

    [Fact]
    public async Task Sends_title_and_author_as_one_escaped_term_for_the_configured_store()
    {
        var handler = new Recorder(HttpStatusCode.OK, """{"results":[]}""");

        await Catalog(handler, "gb").SearchAsync("Rivers & Roads?", "Ines Calder", Ct);

        var query = handler.Uris.Single().Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal("Rivers%20%26%20Roads%3F%20Ines%20Calder", query["term"]);
        Assert.Equal(("ebook", "ebook", "gb", "10"), (query["media"], query["entity"], query["country"], query["limit"]));
        Assert.Equal(5, query.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Slow_down_and_server_errors_pause(HttpStatusCode status) =>
        await Assert.ThrowsAsync<BackendUnavailableException>(() => Catalog(new Recorder(status, "")).SearchAsync("A Title", null, Ct));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task Other_answers_fail_only_this_title(HttpStatusCode status) =>
        await Assert.ThrowsAsync<CatalogLookupFailedException>(() => Catalog(new Recorder(status, "")).SearchAsync("A Title", null, Ct));

    [Fact]
    public async Task An_oversized_answer_fails_only_this_title() =>
        await Assert.ThrowsAsync<CatalogLookupFailedException>(
            () => Catalog(new Recorder(HttpStatusCode.OK, new string(' ', 2 * 1024 * 1024))).SearchAsync("A Title", null, Ct));

    [Theory]
    [InlineData("us", true)]
    [InlineData("gb", true)]
    [InlineData("GB", false)]
    [InlineData("usa", false)]
    [InlineData("us&x=1", false)]
    public void The_store_must_be_a_two_letter_country_code(string country, bool valid)
    {
        var options = new AppleBooksOptions { Country = country };

        Assert.Equal(valid, !options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).Any());
    }

    [Fact]
    public async Task As_a_fallback_it_comes_after_the_primary_with_its_own_status_row()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BookLookup:Fallbacks:0"] = "applebooks" }).Build();
        var handler = new Recorder(HttpStatusCode.OK, "");
        var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddPbrCore(config).AddPbrBookLookup(config);
        services.AddHttpClient(AppleBooksCatalog.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        Assert.Equal([BookLookupProviders.OpenLibrary, BookLookupProviders.AppleBooks],
            provider.GetRequiredService<BookCatalogChain>().All.Select(c => c.Provider));
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Name == BackendHealth.BookLookupFallback("Apple Books"), Ct);
        Assert.Equal(HealthStatus.Healthy, Assert.Single(report.Entries).Value.Status);
    }

    private static AppleBooksCatalog Catalog(HttpMessageHandler handler, string country = "us") =>
        new(new SingleClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://itunes.example.test/") }),
            Options.Create(new AppleBooksOptions { Country = country }), TimeProvider.System) { RequestGap = TimeSpan.Zero };

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
