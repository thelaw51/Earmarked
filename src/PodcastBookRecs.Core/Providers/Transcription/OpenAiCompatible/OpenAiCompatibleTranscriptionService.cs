using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Transcription;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Core.Providers.Transcription.OpenAiCompatible;

/// <summary>
/// Talks to any server implementing OpenAI's <c>POST /v1/audio/transcriptions</c> with <c>verbose_json</c> output:
/// whisper.cpp's server, MLX servers, LM Studio, or a hosted API. Nothing outside this folder knows the wire format.
/// </summary>
public sealed class OpenAiCompatibleTranscriptionService(IHttpClientFactory httpFactory, IOptions<TranscriptionOptions> options) : ITranscriptionService
{
    public const string HttpClientName = "transcription";
    private const string TranscriptionsPath = "v1/audio/transcriptions";

    public string Provider => TranscriptionProviders.OpenAiCompatible;

    public string Model => options.Value.Model;

    /// <summary>Replaces <see cref="BackendProbe.RecoveryWindow"/> in tests.</summary>
    internal TimeSpan RecoveryWindow { get; set; } = BackendProbe.RecoveryWindow;

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "application/octet-stream");
        content.Add(file, "file", fileName);
        content.Add(new StringContent(Model), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        content.Add(new StringContent("0"), "temperature");
        if (!string.IsNullOrWhiteSpace(options.Value.Language))
        {
            content.Add(new StringContent(options.Value.Language), "language");
        }

        HttpResponseMessage response;
        try
        {
            response = await httpFactory.CreateClient(HttpClientName).PostAsync(TranscriptionsPath, content, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new BackendUnavailableException($"Can't reach the transcription backend: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranscriptionFailedException($"Transcription request timed out after {options.Value.RequestTimeoutMinutes} minutes.", ex);
        }
        catch (HttpRequestException ex)
        {
            // The connection broke mid-request. Either the backend went away (stopped, or the Mac slept), which isn't
            // this episode's fault, or it crashed on this audio and its supervisor restarted it, which is.
            if (!await BackendProbe.AnswersWithinAsync(httpFactory.CreateClient(HttpClientName), RecoveryWindow, cancellationToken))
            {
                throw new BackendUnavailableException($"The transcription backend went away mid-request: {ex.Message}", ex);
            }

            throw new TranscriptionFailedException($"Transcription request failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                throw new BackendUnavailableException($"Transcription backend returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranscriptionFailedException($"Transcription backend returned HTTP {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            }

            return Parse(body);
        }
    }

    /// <summary>Maps a <c>verbose_json</c> body to our own types. Falls back to one untimed segment if a server omits segments.</summary>
    internal static TranscriptionResult Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var segments = new List<TranscriptSegment>();
            if (root.TryGetProperty("segments", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in array.EnumerateArray())
                {
                    var text = s.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                    if (text.Length > 0)
                    {
                        segments.Add(new TranscriptSegment(Seconds(s, "start"), Seconds(s, "end"), null, text));
                    }
                }
            }
            else if (root.TryGetProperty("text", out var whole) && whole.ValueKind == JsonValueKind.String && whole.GetString()!.Trim() is { Length: > 0 } wholeText)
            {
                segments.Add(new TranscriptSegment(null, null, null, wholeText));
            }

            var language = root.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            return new TranscriptionResult(segments, language, Seconds(root, "duration"));
        }
        catch (JsonException ex)
        {
            throw new TranscriptionFailedException("Transcription backend returned a response that isn't valid JSON.", ex);
        }

        static TimeSpan? Seconds(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.GetDouble() >= 0
                ? TimeSpan.FromSeconds(value.GetDouble())
                : null;
    }
}

/// <summary>Reports whether the backend answers at all.</summary>
public sealed class OpenAiCompatibleHealthCheck(IHttpClientFactory httpFactory, IOptions<TranscriptionOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problem = await BackendProbe.ProbeAsync(httpFactory.CreateClient(OpenAiCompatibleTranscriptionService.HttpClientName), cancellationToken);
        return problem is null
            ? HealthCheckResult.Healthy($"{TranscriptionProviders.OpenAiCompatible} · {options.Value.Model}")
            : HealthCheckResult.Unhealthy($"Unreachable: {problem}");
    }
}
