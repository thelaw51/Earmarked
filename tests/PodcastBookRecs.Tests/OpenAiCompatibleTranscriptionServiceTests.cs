using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Providers.Transcription.OpenAiCompatible;
using PodcastBookRecs.Core.Transcription;

namespace PodcastBookRecs.Tests;

public class OpenAiCompatibleTranscriptionServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Maps_a_recorded_verbose_json_response_to_segments()
    {
        var result = OpenAiCompatibleTranscriptionService.Parse(Fixtures.Read("whisper-verbose.json"));

        Assert.Equal(3, result.Segments.Count);
        Assert.Equal(TimeSpan.FromSeconds(3.15), result.Segments[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(5.3), result.Segments[1].End);
        Assert.Equal("books that changed how I think about", result.Segments[1].Text);
        Assert.All(result.Segments, s => Assert.Null(s.Speaker));
        Assert.Equal("english", result.Language);
        Assert.NotNull(result.Duration);
    }

    [Fact]
    public void Falls_back_to_one_untimed_segment_when_a_server_omits_segments()
    {
        var result = OpenAiCompatibleTranscriptionService.Parse("""{"text":"  Hello there.  "}""");

        Assert.Equal("Hello there.", Assert.Single(result.Segments).Text);
        Assert.Null(result.Segments[0].Start);
    }

    [Fact]
    public void Rejects_a_body_that_is_not_json() =>
        Assert.Throws<TranscriptionFailedException>(() => OpenAiCompatibleTranscriptionService.Parse("<html>oops</html>"));

    [Fact]
    public async Task Sends_the_audio_and_settings_as_multipart()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, Fixtures.Read("whisper-verbose.json"));
        var service = Service(handler, language: "en");

        await service.TranscribeAsync(new MemoryStream([1, 2, 3]), "chunk_000.wav", Ct);

        Assert.Equal(new Uri("http://transcriber.test:8178/v1/audio/transcriptions"), handler.LastUri);
        Assert.Contains("name=file; filename=chunk_000.wav", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("verbose_json", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("name=language", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("name=model", handler.LastBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Treats_gateway_errors_as_backend_unavailable(HttpStatusCode status) =>
        await Assert.ThrowsAsync<BackendUnavailableException>(() =>
            Service(new RecordingHandler(status, "")).TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Treats_other_errors_as_a_failed_request(HttpStatusCode status) =>
        await Assert.ThrowsAsync<TranscriptionFailedException>(() =>
            Service(new RecordingHandler(status, "bad audio")).TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));

    [Fact]
    public async Task A_refused_connection_means_the_backend_is_unavailable()
    {
        using var provider = RealServices("http://127.0.0.1:9");
        var service = provider.GetRequiredService<ITranscriptionService>();

        await Assert.ThrowsAsync<BackendUnavailableException>(() => service.TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
    }

    [Fact]
    public async Task A_connect_timeout_means_the_backend_is_unavailable()
    {
        // 10.255.255.1 is non-routable, so connecting hangs until our own 10 s connect timeout. Some networks reject it
        // straight away instead, which must also count as "unavailable".
        using var provider = RealServices("http://10.255.255.1:8178");
        var service = provider.GetRequiredService<ITranscriptionService>();

        var ex = await Assert.ThrowsAsync<BackendUnavailableException>(() => service.TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
        Assert.Contains("Can't reach", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_that_times_out_after_connecting_counts_as_a_failure()
    {
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        using var provider = RealServices($"http://127.0.0.1:{port}", timeoutOverride: TimeSpan.FromSeconds(1));
        var service = provider.GetRequiredService<ITranscriptionService>();

        await Assert.ThrowsAsync<TranscriptionFailedException>(() => service.TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
    }

    [Fact]
    public async Task Health_check_reports_unreachable_without_throwing()
    {
        using var provider = RealServices("http://127.0.0.1:9");
        var check = ActivatorUtilities.CreateInstance<OpenAiCompatibleHealthCheck>(provider);

        var result = await check.CheckHealthAsync(new HealthCheckContext(), Ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.StartsWith("Unreachable", result.Description, StringComparison.Ordinal);
    }

    private static OpenAiCompatibleTranscriptionService Service(HttpMessageHandler handler, string? language = null) =>
        new(new SingleClientFactory(new HttpClient(handler) { BaseAddress = new Uri("http://transcriber.test:8178/") }),
            Options.Create(new TranscriptionOptions { Provider = "OpenAiCompatible", BaseUrl = "http://transcriber.test:8178", Language = language }));

    private static ServiceProvider RealServices(string baseUrl, TimeSpan? timeoutOverride = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transcription:Provider"] = "OpenAiCompatible",
                ["Transcription:BaseUrl"] = baseUrl,
            })
            .Build();
        var services = new ServiceCollection().AddLogging().AddPbrCore(config).AddPbrTranscription(config);
        if (timeoutOverride is not null)
        {
            services.AddHttpClient(OpenAiCompatibleTranscriptionService.HttpClientName, c => c.Timeout = timeoutOverride.Value);
        }

        return services.BuildServiceProvider();
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
