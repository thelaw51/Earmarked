using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Earmarked.Core.Backends;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Extraction;
using Earmarked.Core.Providers.Extraction.OpenAiCompatible;

namespace Earmarked.Tests;

public class OpenAiCompatibleBookExtractorTests
{
    private static readonly ExtractionInput Input = new("A Show", "Episode 1", "[00:00] some words");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Maps_a_recorded_answer_to_mentions_and_skips_unknown_kinds()
    {
        var mentions = OpenAiCompatibleBookExtractor.Parse(Fixtures.Read("chat-completion-mentions.json"));

        Assert.Equal(2, mentions.Count);
        Assert.Equal(new ExtractedMention("The Lighthouse Keeper's Almanac", "Ines Calder",
            "I wrote The Lighthouse Keeper's Almanac after a winter on the coast.", MentionKind.OwnBook, 0.95), mentions[0]);
        Assert.Null(mentions[1].Author);
        Assert.Equal(MentionKind.Recommended, mentions[1].Kind);
    }

    [Fact]
    public void Caps_runaway_answers()
    {
        var many = Enumerable.Range(0, 80).Select(i => new { title = new string('t', 500), author = (string?)null, quote = $"quote {i}", kind = "passing", confidence = 7 });
        var body = Completion(JsonSerializer.Serialize(new { mentions = many }));

        var mentions = OpenAiCompatibleBookExtractor.Parse(body);

        Assert.Equal(OpenAiCompatibleBookExtractor.MaxMentionsPerWindow, mentions.Count);
        Assert.All(mentions, m => Assert.Equal(BookMention.MaxTitleLength, m.Title.Length));
        Assert.All(mentions, m => Assert.Equal(1, m.Confidence));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"choices":[]}""")]
    public void Rejects_a_body_that_is_not_a_completion(string body) =>
        Assert.Throws<ExtractionFailedException>(() => OpenAiCompatibleBookExtractor.Parse(body));

    [Fact]
    public void Rejects_an_answer_that_is_not_the_schema() =>
        Assert.Throws<ExtractionFailedException>(() => OpenAiCompatibleBookExtractor.Parse(Completion("""{"books":"none"}""")));

    [Fact]
    public void Rejects_an_answer_cut_off_by_the_token_limit()
    {
        var body = Completion("""{"mentions":[""", finishReason: "length");

        var ex = Assert.Throws<ExtractionFailedException>(() => OpenAiCompatibleBookExtractor.Parse(body));
        Assert.Contains("cut off", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sends_the_prompt_transcript_and_a_strict_schema()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, Fixtures.Read("chat-completion-mentions.json"));

        await Extractor(handler).ExtractAsync(Input, Ct);

        Assert.Equal(new Uri("http://llm.test:1234/v1/chat/completions"), handler.LastUri);
        using var request = JsonDocument.Parse(handler.LastBody);
        var root = request.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.Equal(ExtractionPrompt.Text, root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("<transcript>\n[00:00] some words\n</transcript>", root.GetProperty("messages")[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var kinds = format.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("mentions")
            .GetProperty("items").GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(k => k.GetString());
        Assert.Equal(ExtractionPrompt.Kinds.Keys.Order(), kinds.Order());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_missing_model_or_gateway_error_means_the_backend_is_unavailable(HttpStatusCode status) =>
        await Assert.ThrowsAsync<BackendUnavailableException>(() => Extractor(new RecordingHandler(status, "")).ExtractAsync(Input, Ct));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Other_errors_count_as_a_failed_request(HttpStatusCode status) =>
        await Assert.ThrowsAsync<ExtractionFailedException>(() => Extractor(new RecordingHandler(status, "context too long")).ExtractAsync(Input, Ct));

    [Fact]
    public async Task A_refused_connection_means_the_backend_is_unavailable()
    {
        using var provider = RealServices("http://127.0.0.1:9");

        await Assert.ThrowsAsync<BackendUnavailableException>(() => provider.GetRequiredService<IBookExtractor>().ExtractAsync(Input, Ct));
    }

    [Theory]
    [InlineData("""{"data":[{"id":"other"},{"id":"TEST-MODEL"}]}""", HealthStatus.Healthy)]
    [InlineData("""{"data":[{"id":"other"}]}""", HealthStatus.Unhealthy)]
    public async Task Health_check_looks_for_the_configured_model(string models, HealthStatus expected)
    {
        var check = new OpenAiCompatibleExtractionHealthCheck(new SingleClientFactory(Client(new RecordingHandler(HttpStatusCode.OK, models))), Options());

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Health_check_reports_unreachable_without_throwing()
    {
        using var provider = RealServices("http://127.0.0.1:9");
        var check = ActivatorUtilities.CreateInstance<OpenAiCompatibleExtractionHealthCheck>(provider);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.StartsWith("Unreachable", result.Description, StringComparison.Ordinal);
    }

    private static string Completion(string content, string finishReason = "stop") =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finishReason } } });

    private static IOptions<ExtractionOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new ExtractionOptions { Provider = "OpenAiCompatible", BaseUrl = "http://llm.test:1234", Model = "test-model" });

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://llm.test:1234/") };

    private static OpenAiCompatibleBookExtractor Extractor(HttpMessageHandler handler) => new(new SingleClientFactory(Client(handler)), Options());

    private static ServiceProvider RealServices(string baseUrl)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Extraction:Provider"] = "OpenAiCompatible", ["Extraction:BaseUrl"] = baseUrl })
            .Build();
        return new ServiceCollection().AddLogging().AddEarmarkedCore(config).AddEarmarkedExtraction(config).BuildServiceProvider();
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
