using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Extraction;

namespace PodcastBookRecs.Core.Providers.Extraction.OpenAiCompatible;

/// <summary>
/// Talks to any server implementing OpenAI's <c>POST /v1/chat/completions</c> with <c>json_schema</c> structured
/// output: LM Studio, Ollama, llama.cpp's server or a hosted API. Nothing outside this folder knows the wire format.
/// </summary>
public sealed class OpenAiCompatibleBookExtractor(IHttpClientFactory httpFactory, IOptions<ExtractionOptions> options) : IBookExtractor
{
    public const string HttpClientName = "extraction";

    /// <summary>More than this many books in one window means the answer has gone wrong; the rest are dropped.</summary>
    internal const int MaxMentionsPerWindow = 50;

    private const string CompletionsPath = "v1/chat/completions";
    private const int MaxAnswerTokens = 2048;

    private static readonly JsonObject Schema = BuildSchema();

    public string Provider => ExtractionProviders.OpenAiCompatible;

    public string Model => options.Value.Model;

    public string PromptVersion => ExtractionPrompt.Version;

    /// <summary>Replaces <see cref="BackendProbe.RecoveryWindow"/> in tests.</summary>
    internal TimeSpan RecoveryWindow { get; set; } = BackendProbe.RecoveryWindow;

    public async Task<IReadOnlyList<ExtractedMention>> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0,
            ["max_tokens"] = MaxAnswerTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = ExtractionPrompt.Text },
                new JsonObject { ["role"] = "user", ["content"] = ExtractionPrompt.UserMessage(input) },
            },
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "book_mentions", ["strict"] = true, ["schema"] = Schema.DeepClone() },
            },
        };

        var client = httpFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(CompletionsPath, request, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new BackendUnavailableException($"Can't reach the extraction backend: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExtractionFailedException($"Extraction request timed out after {options.Value.RequestTimeoutMinutes} minutes.", ex);
        }
        catch (HttpRequestException ex)
        {
            // The connection broke mid-request: the backend went away (pause), or crashed on this input and was restarted (count it).
            if (!await BackendProbe.AnswersWithinAsync(client, RecoveryWindow, cancellationToken))
            {
                throw new BackendUnavailableException($"The extraction backend went away mid-request: {ex.Message}", ex);
            }

            throw new ExtractionFailedException($"Extraction request failed: {ex.Message}", ex);
        }

        using (response)
        {
            // 404 means the configured model isn't there. That's a setup problem, so pause rather than fail every episode.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                throw new BackendUnavailableException($"Extraction backend returned HTTP {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ExtractionFailedException($"Extraction backend returned HTTP {(int)response.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            }

            return Parse(body);
        }
    }

    /// <summary>
    /// Reads the mentions out of a chat completion. Entries with an unknown kind or no title or quote are skipped;
    /// a body that isn't the expected shape fails the request. Lengths are capped to what the database stores.
    /// </summary>
    internal static IReadOnlyList<ExtractedMention> Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var choice = doc.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
            {
                throw new ExtractionFailedException($"The model's answer was cut off at {MaxAnswerTokens} tokens.");
            }

            var content = choice.GetProperty("message").GetProperty("content").GetString()
                ?? throw new ExtractionFailedException("The model's answer was empty.");
            using var answer = JsonDocument.Parse(content);
            var mentions = new List<ExtractedMention>();
            foreach (var m in answer.RootElement.GetProperty("mentions").EnumerateArray().Take(MaxMentionsPerWindow))
            {
                var title = String(m, "title");
                var quote = String(m, "quote");
                if (title is null || quote is null || !ExtractionPrompt.Kinds.TryGetValue(String(m, "kind") ?? "", out var kind))
                {
                    continue;
                }

                var confidence = m.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? Math.Clamp(c.GetDouble(), 0, 1) : 0;
                mentions.Add(new ExtractedMention(
                    Cap(title, BookMention.MaxTitleLength),
                    String(m, "author") is { } author ? Cap(author, BookMention.MaxAuthorLength) : null,
                    Cap(quote, BookMention.MaxQuoteLength),
                    kind,
                    confidence));
            }

            return mentions;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new ExtractionFailedException("Extraction backend returned an answer that isn't the expected JSON.", ex);
        }

        static string? String(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

        static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
    }

    private static JsonObject BuildSchema()
    {
        var kinds = new JsonArray();
        foreach (var name in ExtractionPrompt.Kinds.Keys)
        {
            kinds.Add(name);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["mentions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["title"] = new JsonObject { ["type"] = "string" },
                            ["author"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                            ["quote"] = new JsonObject { ["type"] = "string" },
                            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = kinds },
                            ["confidence"] = new JsonObject { ["type"] = "number" },
                        },
                        ["required"] = new JsonArray("title", "author", "quote", "kind", "confidence"),
                        ["additionalProperties"] = false,
                    },
                },
            },
            ["required"] = new JsonArray("mentions"),
            ["additionalProperties"] = false,
        };
    }
}

/// <summary>Reports whether the backend answers and lists the configured model.</summary>
public sealed class OpenAiCompatibleExtractionHealthCheck(IHttpClientFactory httpFactory, IOptions<ExtractionOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var model = options.Value.Model;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(BackendHealth.Timeout);
        try
        {
            using var response = await httpFactory.CreateClient(OpenAiCompatibleBookExtractor.HttpClientName).GetAsync("v1/models", cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                // Answers, but has no model list: some servers don't implement it. Reachable is all we can say.
                return HealthCheckResult.Healthy($"{ExtractionProviders.OpenAiCompatible} · {model}");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var listed = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.EnumerateArray().Any(m => m.TryGetProperty("id", out var id) && string.Equals(id.GetString(), model, StringComparison.OrdinalIgnoreCase));
            return listed
                ? HealthCheckResult.Healthy($"{ExtractionProviders.OpenAiCompatible} · {model}")
                : HealthCheckResult.Unhealthy($"Reachable, but model '{model}' isn't available.");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return HealthCheckResult.Unhealthy(ex is OperationCanceledException ? "Unreachable: no answer within 5 seconds." : $"Unreachable: {ex.Message}");
        }
        catch (JsonException)
        {
            return HealthCheckResult.Healthy($"{ExtractionProviders.OpenAiCompatible} · {model}");
        }
    }
}
