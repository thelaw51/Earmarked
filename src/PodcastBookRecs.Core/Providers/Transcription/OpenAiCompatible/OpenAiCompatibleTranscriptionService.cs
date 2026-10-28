using System.Buffers.Binary;
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

    /// <summary>How long the test clip sent after a 5xx may take. One second of audio answers in well under that.</summary>
    internal static readonly TimeSpan TestClipTimeout = TimeSpan.FromMinutes(1);

    private int _rejectingAudioStatus;

    /// <summary>
    /// The HTTP status the backend gave both a chunk and the test clip, until anything transcribes again; 0 when it
    /// isn't rejecting audio. Only the health check reads it.
    /// </summary>
    internal int RejectingAudioStatus => Volatile.Read(ref _rejectingAudioStatus);

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(audio, fileName, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            throw new BackendUnavailableException($"Transcription backend returned HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500)
            {
                await EnsureBackendTakesAudioAsync((int)response.StatusCode, cancellationToken);
            }

            throw new TranscriptionFailedException($"Transcription backend returned HTTP {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }

        Volatile.Write(ref _rejectingAudioStatus, 0);
        return Parse(body);
    }

    /// <summary>
    /// After a 5xx on a chunk, sends <see cref="TestClip.Wav"/>. whisper.cpp gives the same 500 for one file it can't
    /// decode and for a server that is broken for every file. If the clip transcribes, the chunk was at fault and its
    /// attempt counts. If the clip also gets a 5xx, fails or times out, the backend is broken: that pauses
    /// transcription without counting, as an unreachable backend does. A 4xx for the clip can't tell the two apart, so
    /// the attempt counts, and audio that keeps failing still ends up Failed instead of pausing forever.
    /// </summary>
    private async Task EnsureBackendTakesAudioAsync(int chunkStatus, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TestClipTimeout);
        try
        {
            using var clip = new MemoryStream(TestClip.Wav, writable: false);
            using var response = await SendAsync(clip, "test-clip.wav", cts.Token);
            if (response.IsSuccessStatusCode)
            {
                Volatile.Write(ref _rejectingAudioStatus, 0);
                return;
            }

            if ((int)response.StatusCode < 500)
            {
                return;
            }
        }
        catch (TranscriptionFailedException)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        Volatile.Write(ref _rejectingAudioStatus, chunkStatus);
        throw new BackendUnavailableException($"Transcription backend answers, but rejects all audio: HTTP {chunkStatus} for this chunk and for a test clip.");
    }

    /// <summary>Posts one file. Maps "can't connect" and "went away mid-request" to <see cref="BackendUnavailableException"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(Stream audio, string fileName, CancellationToken cancellationToken)
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

        try
        {
            return await httpFactory.CreateClient(HttpClientName).PostAsync(TranscriptionsPath, content, cancellationToken);
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

/// <summary>
/// Reports whether the backend answers at all, and whether the last transcription found it rejecting every file. It
/// sends no audio itself: the transcriber's own retries clear that state once a chunk or the test clip transcribes.
/// </summary>
public sealed class OpenAiCompatibleHealthCheck(
    IHttpClientFactory httpFactory,
    OpenAiCompatibleTranscriptionService transcription,
    IOptions<TranscriptionOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var problem = await BackendProbe.ProbeAsync(httpFactory.CreateClient(OpenAiCompatibleTranscriptionService.HttpClientName), cancellationToken);
        if (problem is not null)
        {
            return HealthCheckResult.Unhealthy($"Unreachable: {problem}");
        }

        return transcription.RejectingAudioStatus is var status and > 0
            ? HealthCheckResult.Unhealthy($"Answers, but rejects all audio (HTTP {status}). Restart the transcription server.")
            : HealthCheckResult.Healthy($"{TranscriptionProviders.OpenAiCompatible} · {options.Value.Model}");
    }
}

/// <summary>
/// One second of a quiet 440 Hz tone as a 16 kHz mono 16-bit WAV, built in code. A tone rather than silence, because
/// a server with voice detection may refuse a file with no sound at all.
/// </summary>
internal static class TestClip
{
    private const int SampleRate = 16_000;

    public static readonly byte[] Wav = Build();

    private static byte[] Build()
    {
        const int dataBytes = SampleRate * 2;
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);
        for (var i = 0; i < SampleRate; i++)
        {
            var sample = (short)(300 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * 2))..], sample);
        }

        return wav;
    }
}
