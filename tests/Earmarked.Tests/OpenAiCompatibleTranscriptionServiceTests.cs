using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Earmarked.Core.Backends;
using Earmarked.Core.Configuration;
using Earmarked.Core.Providers.Transcription.OpenAiCompatible;
using Earmarked.Core.Transcription;

namespace Earmarked.Tests;

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

    [Fact]
    public async Task A_client_error_counts_without_sending_the_test_clip()
    {
        var handler = new SequenceHandler((HttpStatusCode.BadRequest, "bad audio"));

        await Assert.ThrowsAsync<TranscriptionFailedException>(() => Service(handler).TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
        Assert.Equal(["a.wav"], handler.FileNames);
    }

    [Fact]
    public async Task A_server_error_on_one_chunk_counts_when_the_test_clip_transcribes()
    {
        // whisper.cpp answers 500 for a single file it can't decode.
        var handler = new SequenceHandler((HttpStatusCode.InternalServerError, """{"error":"failed to process audio"}"""), (HttpStatusCode.OK, """{"text":""}"""));
        var service = Service(handler);

        var ex = await Assert.ThrowsAsync<TranscriptionFailedException>(() => service.TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
        Assert.Contains("HTTP 500", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["a.wav", "test-clip.wav"], handler.FileNames);
        Assert.Equal(0, service.RejectingAudioStatus);
    }

    [Fact]
    public async Task A_server_error_for_the_test_clip_too_pauses_and_shows_on_the_health_check()
    {
        // What the deployed whisper did for six hours: 500 for every file, while still answering a GET.
        const string Broken = """{"error":"failed to process audio"}""";
        var handler = new SequenceHandler((HttpStatusCode.InternalServerError, Broken), (HttpStatusCode.InternalServerError, Broken), (HttpStatusCode.OK, Fixtures.Read("whisper-verbose.json")));
        var service = Service(handler);
        var check = HealthCheck(handler, service);

        var ex = await Assert.ThrowsAsync<BackendUnavailableException>(() => service.TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
        Assert.Contains("rejects all audio", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed to process", ex.Message, StringComparison.Ordinal);
        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.Equal("Answers, but rejects all audio (HTTP 500). Restart the transcription server.", unhealthy.Description);

        await service.TranscribeAsync(new MemoryStream([1]), "a.wav", Ct);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status);
    }

    [Fact]
    public async Task A_client_error_for_the_test_clip_counts_the_attempt()
    {
        // The clip can't tell a broken backend from a bad chunk here, so the attempt counts and never pauses forever.
        var handler = new SequenceHandler((HttpStatusCode.InternalServerError, "oops"), (HttpStatusCode.BadRequest, "no"));
        var service = Service(handler);

        await Assert.ThrowsAsync<TranscriptionFailedException>(() => service.TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
        Assert.Equal(0, service.RejectingAudioStatus);
    }

    [Fact]
    public async Task A_gateway_error_pauses_without_sending_the_test_clip()
    {
        var handler = new SequenceHandler((HttpStatusCode.BadGateway, ""));

        await Assert.ThrowsAsync<BackendUnavailableException>(() => Service(handler).TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
        Assert.Equal(["a.wav"], handler.FileNames);
    }

    [Fact]
    public void The_test_clip_is_one_second_of_16_khz_mono_pcm_with_sound_in_it()
    {
        var wav = TestClip.Wav;

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(16_000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(32_000, BitConverter.ToInt32(wav, 40));
        Assert.Contains(wav.Skip(44), b => b != 0);
    }

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
    public async Task A_backend_that_hangs_up_mid_request_and_stays_down_is_unavailable()
    {
        // What happens when whisper-server is stopped, or the Mac sleeps, while it works on a chunk.
        using var server = new DroppingServer(answerAfterDrop: false);
        using var provider = RealServices(server.BaseUrl);
        var service = (OpenAiCompatibleTranscriptionService)provider.GetRequiredService<ITranscriptionService>();
        service.RecoveryWindow = TimeSpan.FromSeconds(1);

        var ex = await Assert.ThrowsAsync<BackendUnavailableException>(() => service.TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
        Assert.Contains("went away mid-request", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backend_that_hangs_up_mid_request_but_still_answers_counts_as_a_failure()
    {
        // What a crash on this audio looks like: launchd restarts the server, so it answers again. Counting it means
        // audio that always crashes the server ends up Failed instead of pausing transcription forever.
        using var server = new DroppingServer(answerAfterDrop: true);
        using var provider = RealServices(server.BaseUrl);

        await Assert.ThrowsAsync<TranscriptionFailedException>(() =>
            provider.GetRequiredService<ITranscriptionService>().TranscribeAsync(new MemoryStream([1]), "a.wav", Ct));
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

    private static OpenAiCompatibleHealthCheck HealthCheck(HttpMessageHandler handler, OpenAiCompatibleTranscriptionService service) =>
        new(new SingleClientFactory(new HttpClient(handler) { BaseAddress = new Uri("http://transcriber.test:8178/") }), service,
            Options.Create(new TranscriptionOptions { Provider = "OpenAiCompatible", BaseUrl = "http://transcriber.test:8178" }));

    private static ServiceProvider RealServices(string baseUrl, TimeSpan? timeoutOverride = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transcription:Provider"] = "OpenAiCompatible",
                ["Transcription:BaseUrl"] = baseUrl,
            })
            .Build();
        var services = new ServiceCollection().AddLogging().AddEarmarkedCore(config).AddEarmarkedTranscription(config);
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

    /// <summary>
    /// Answers each POST with the next response in order (repeating the last one) and every GET, the health probe,
    /// with 200. Records the file name of each POST.
    /// </summary>
    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<string> FileNames { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            FileNames.Add(System.Text.RegularExpressions.Regex.Match(body, "filename=([^;\\r\\n]+)").Groups[1].Value);
            var (status, text) = responses[Math.Min(_next++, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(text) };
        }
    }

    /// <summary>
    /// Reads the first request in full, then closes the connection without answering. Afterwards it either stops
    /// listening (the backend is gone) or answers every request with 200 (it was restarted).
    /// </summary>
    private sealed class DroppingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public DroppingServer(bool answerAfterDrop)
        {
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = RunAsync(answerAfterDrop);
        }

        public string BaseUrl { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task RunAsync(bool answerAfterDrop)
        {
            try
            {
                using (var first = await _listener.AcceptTcpClientAsync(_stop.Token))
                {
                    await ReadRequestAsync(first.GetStream());
                }

                if (!answerAfterDrop)
                {
                    _listener.Stop();
                    return;
                }

                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    await ReadRequestAsync(stream);
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
            {
            }
        }

        /// <summary>Reads the headers and the Content-Length body, so closing afterwards ends the response cleanly instead of resetting.</summary>
        private async Task ReadRequestAsync(NetworkStream stream)
        {
            var received = new List<byte>();
            var buffer = new byte[8192];
            int headerEnd;
            while ((headerEnd = System.Text.Encoding.ASCII.GetString([.. received]).IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
            {
                var read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                received.AddRange(buffer.AsSpan(0, read));
            }

            var headers = System.Text.Encoding.ASCII.GetString([.. received], 0, headerEnd);
            var lengthLine = headers.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var remaining = (lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
                - (received.Count - headerEnd - 4);
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), _stop.Token);
                if (read == 0)
                {
                    return;
                }

                remaining -= read;
            }
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
