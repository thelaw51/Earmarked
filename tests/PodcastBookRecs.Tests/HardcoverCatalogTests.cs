using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Providers.BookLookup.Hardcover;

namespace PodcastBookRecs.Tests;

/// <summary>The Hardcover adapter against an invented recorded search and a stub handler.</summary>
public sealed class HardcoverCatalogTests
{
    private const string Token = "hc_pat_test-placeholder";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Maps_a_recorded_search_to_candidates_and_skips_unusable_entries()
    {
        var books = HardcoverCatalog.Parse(Fixtures.Read("hardcover-search.json"));

        Assert.Equal(["700001", "700002", "700006"], books.Select(b => b.CatalogId));
        var first = books[0];
        Assert.Equal(("Harbour Lights", "A Novel", (int?)2025, (int?)null, BookLookupProviders.Hardcover),
            (first.Title, first.Subtitle, first.FirstPublishYear, first.EditionCount, first.Provider));
        Assert.Equal(["Ines Calder", "Tomas Reyne"], first.Authors);
        Assert.Equal("https://hardcover.app/books/harbour-lights", first.InfoUrl);
        Assert.Equal("https://assets.hardcover.app/edition/1/cover.jpeg", first.CoverUrl);
        Assert.Null(books[1].FirstPublishYear);
        Assert.All(books.Skip(1), b => Assert.Null(b.CoverUrl));
        Assert.All(books, b => Assert.True(CatalogMatcher.IsUsable(b)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"data":{"search":{"results":{"found":0}}}}""")]
    [InlineData("""{"data":null}""")]
    public void Rejects_a_body_that_is_not_a_search_result(string body) =>
        Assert.Throws<CatalogLookupFailedException>(() => HardcoverCatalog.Parse(body));

    [Fact]
    public void A_graphql_error_fails_the_title_without_repeating_the_errors_text()
    {
        var ex = Assert.Throws<CatalogLookupFailedException>(() => HardcoverCatalog.Parse(
            """{"errors":[{"message":"bad input 'Harbour Lights'","extensions":{"code":"validation-failed"}}]}"""));

        Assert.Equal("Hardcover returned an error (validation-failed).", ex.Message);
    }

    [Fact]
    public void A_graphql_error_code_that_isnt_a_plain_word_is_left_out() =>
        Assert.Equal("Hardcover returned an error.", Assert.Throws<CatalogLookupFailedException>(() => HardcoverCatalog.Parse(
            """{"errors":[{"message":"x","extensions":{"code":"Harbour Lights by Ines Calder"}}]}""")).Message);

    [Fact]
    public async Task Posts_the_fixed_query_with_the_term_only_in_its_variables_and_a_bearer_token()
    {
        var handler = new Recorder(HttpStatusCode.OK, """{"data":{"search":{"results":{"hits":[]}}}}""");

        await Catalog(handler).SearchAsync("Rivers \" } Roads", "Ines Calder", Ct);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "https://graphql.example.test/v1/graphql"), (sent.Method, sent.Uri.ToString()));
        Assert.Equal($"Bearer {Token}", sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(HardcoverCatalog.Query, body.RootElement.GetProperty("query").GetString());
        Assert.Equal("Rivers \" } Roads Ines Calder", body.RootElement.GetProperty("variables").GetProperty("q").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("variables").GetProperty("n").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Slow_down_and_server_errors_pause(HttpStatusCode status) =>
        await Assert.ThrowsAsync<BackendUnavailableException>(() => Catalog(new Recorder(status, "")).SearchAsync("A Title", null, Ct));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_rejected_token_or_bad_request_fails_only_this_title_and_never_shows_the_token(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<CatalogLookupFailedException>(() => Catalog(new Recorder(status, """{"error":"invalid_token"}""")).SearchAsync("A Title", null, Ct));

        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversized_answer_fails_only_this_title() =>
        await Assert.ThrowsAsync<CatalogLookupFailedException>(
            () => Catalog(new Recorder(HttpStatusCode.OK, new string(' ', 2 * 1024 * 1024))).SearchAsync("A Title", null, Ct));

    [Theory]
    [InlineData(HttpStatusCode.OK, HealthStatus.Healthy, "Hardcover")]
    [InlineData(HttpStatusCode.Unauthorized, HealthStatus.Unhealthy, "Token rejected")]
    [InlineData(HttpStatusCode.Forbidden, HealthStatus.Unhealthy, "read:catalog")]
    [InlineData(HttpStatusCode.TooManyRequests, HealthStatus.Unhealthy, "HTTP 429")]
    public async Task Its_status_row_tests_the_token_and_reuses_any_answer(HttpStatusCode status, HealthStatus expected, string says)
    {
        var handler = new Recorder(status, "");
        var time = new ManualTime();
        var check = new HardcoverHealthCheck(new SingleClientFactory(Client(handler)), Options.Create(new HardcoverOptions { Token = Token }), time);

        var first = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        var second = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        time.Advance(HardcoverHealthCheck.RecheckAfter);
        await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(expected, first.Status);
        Assert.Contains(says, first.Description, StringComparison.Ordinal);
        Assert.Same(first.Description, second.Description);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("hc_pat_abc123", true)]
    [InlineData("Bearer hc_pat_abc123", false)]
    [InlineData("hc_pat_abc\r\nX-Other: 1", false)]
    public void The_token_must_be_set_and_plain(string token, bool valid)
    {
        var options = new HardcoverOptions { Token = token };

        var errors = options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options)).ToList();

        Assert.Equal(valid, errors.Count == 0);
        Assert.All(errors, e => Assert.DoesNotContain("abc", e.ErrorMessage, StringComparison.Ordinal));
    }

    [Fact]
    public async Task As_a_fallback_it_gets_its_own_status_row()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BookLookup:Fallbacks:0"] = "hardcover",
            ["Hardcover:Token"] = Token,
        }).Build();
        var handler = new Recorder(HttpStatusCode.OK, "");
        var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddPbrCore(config).AddPbrBookLookup(config);
        services.AddHttpClient(HardcoverCatalog.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        Assert.Equal([BookLookupProviders.OpenLibrary, BookLookupProviders.Hardcover],
            provider.GetRequiredService<BookCatalogChain>().All.Select(c => c.Provider));
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Name == BackendHealth.BookLookupFallback("Hardcover"), Ct);
        Assert.Equal(HealthStatus.Healthy, Assert.Single(report.Entries).Value.Status);
        Assert.Equal("https://api.hardcover.app/v1/graphql", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hc_pat secret-value")]
    public void Listed_without_a_usable_token_it_refuses_to_start_without_echoing_the_value(string? token)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BookLookup:Fallbacks:0"] = "Hardcover",
            ["Hardcover:Token"] = token,
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddPbrCore(config).AddPbrBookLookup(config).BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<HardcoverOptions>>().Value);

        Assert.DoesNotContain("secret-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_listed_it_needs_no_token_and_isnt_registered()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BookLookup:Fallbacks:0"] = "AppleBooks" }).Build();
        using var provider = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System).AddPbrCore(config).AddPbrBookLookup(config).BuildServiceProvider();

        Assert.Null(provider.GetKeyedService<IBookCatalog>(BookLookupProviders.Hardcover));
        Assert.DoesNotContain(provider.GetRequiredService<BookCatalogChain>().All, c => c.Provider == BookLookupProviders.Hardcover);
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://graphql.example.test/") };

    private static HardcoverCatalog Catalog(HttpMessageHandler handler) =>
        new(new SingleClientFactory(Client(handler)), Options.Create(new HardcoverOptions { Token = Token }), TimeProvider.System) { RequestGap = TimeSpan.Zero };

    private sealed record Sent(HttpMethod Method, Uri Uri, string? Authorization, string Body);

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
