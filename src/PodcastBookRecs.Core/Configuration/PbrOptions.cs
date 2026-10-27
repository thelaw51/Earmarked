using System.ComponentModel.DataAnnotations;

namespace PodcastBookRecs.Core.Configuration;

/// <summary>Where state lives. <c>/data</c> must be local disk, not a network share: SQLite locking breaks over SMB.</summary>
public sealed class PathsOptions
{
    public const string Section = "Paths";

    [Required]
    public string Data { get; set; } = "/data";

    public string DatabaseFile => Path.Combine(Data, "podcastbookrecs.db");
    public string KeysDirectory => Path.Combine(Data, "keys");
}

/// <summary>Feed polling and job retry settings.</summary>
public sealed class PollingOptions
{
    public const string Section = "Polling";

    [Range(5, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>How many of the newest episodes to process when a feed is first added. Older ones are stored as Skipped.</summary>
    [Range(0, 1000)]
    public int BackfillEpisodes { get; set; } = 10;

    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>A feed listed in config.json, imported into the database on Worker startup if not already present.</summary>
public sealed class FeedSeed
{
    [Required]
    public string Url { get; set; } = "";
}

/// <summary>Speech-to-text settings. Transcription is off unless <see cref="Provider"/> is set.</summary>
public sealed class TranscriptionOptions : IValidatableObject
{
    public const string Section = "Transcription";

    /// <summary>Which adapter to use, e.g. <c>OpenAiCompatible</c>. Empty disables transcription.</summary>
    public string? Provider { get; set; }

    /// <summary>The backend's base URL, e.g. <c>http://mac-mini.lan:8178</c>.</summary>
    public string? BaseUrl { get; set; }

    public string Model { get; set; } = "whisper";

    /// <summary>ISO 639-1 code sent with each request. Null lets the backend detect the language.</summary>
    public string? Language { get; set; } = "en";

    /// <summary>Audio is split into chunks of this length, so long episodes resume part-way and no request runs too long.</summary>
    [Range(1, 60)]
    public int ChunkMinutes { get; set; } = 15;

    [Range(1, 120)]
    public int RequestTimeoutMinutes { get; set; } = 10;

    /// <summary>For shows that have published transcripts before, how long a new episode waits for one before being transcribed.</summary>
    [Range(0, 60)]
    public int PublishedTranscriptGraceDays { get; set; } = 3;

    /// <summary>Audio beyond this length is not transcribed. Also caps how much disk ffmpeg's output can use.</summary>
    [Range(1, 24)]
    public int MaxAudioHours { get; set; } = 8;

    [Range(1, 240)]
    public int FfmpegTimeoutMinutes { get; set; } = 30;

    /// <summary>Downloads and splitting wait until at least this much space is free on the data volume.</summary>
    [Range(1, 100)]
    public int MinFreeDiskGb { get; set; } = 3;

    public string FfmpegPath { get; set; } = "ffmpeg";

    public bool Enabled => !string.IsNullOrWhiteSpace(Provider);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (!TranscriptionProviders.Known.Contains(Provider!, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ValidationResult($"Unknown Transcription:Provider '{Provider}'. Known: {string.Join(", ", TranscriptionProviders.Known)}.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult("Transcription:BaseUrl must be an absolute http(s) URL when a provider is set.");
        }
    }
}

/// <summary>Names accepted in <see cref="TranscriptionOptions.Provider"/>.</summary>
public static class TranscriptionProviders
{
    public const string OpenAiCompatible = "OpenAiCompatible";

    public static readonly IReadOnlyList<string> Known = [OpenAiCompatible];
}

/// <summary>Book mention extraction settings. Extraction is off unless <see cref="Provider"/> is set.</summary>
public sealed class ExtractionOptions : IValidatableObject
{
    public const string Section = "Extraction";

    /// <summary>Which adapter to use, e.g. <c>OpenAiCompatible</c>. Empty disables extraction.</summary>
    public string? Provider { get; set; }

    /// <summary>The backend's base URL, e.g. <c>http://mac-mini.lan:1234</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The model name the backend knows, e.g. <c>qwen/qwen3-8b</c> in LM Studio.</summary>
    public string Model { get; set; } = "qwen/qwen3-8b";

    /// <summary>
    /// Words of transcript sent per request. 1,800 words is about 4,300 tokens with the prompt, which fits an 8,192
    /// token context with room for the answer. Smaller windows find more books but take more requests.
    /// </summary>
    [Range(200, 20000)]
    public int WindowWords { get; set; } = 1800;

    /// <summary>Words each window repeats from the end of the previous one, so a mention across a boundary isn't cut in half.</summary>
    [Range(0, 2000)]
    public int OverlapWords { get; set; } = 200;

    /// <summary>Covers loading the model on the first request after it was unloaded, as well as the answer itself.</summary>
    [Range(1, 60)]
    public int RequestTimeoutMinutes { get; set; } = 10;

    public bool Enabled => !string.IsNullOrWhiteSpace(Provider);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (!ExtractionProviders.Known.Contains(Provider!, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ValidationResult($"Unknown Extraction:Provider '{Provider}'. Known: {string.Join(", ", ExtractionProviders.Known)}.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult("Extraction:BaseUrl must be an absolute http(s) URL when a provider is set.");
        }

        if (OverlapWords >= WindowWords / 2)
        {
            yield return new ValidationResult("Extraction:OverlapWords must be less than half of WindowWords.");
        }
    }
}

/// <summary>Names accepted in <see cref="ExtractionOptions.Provider"/>.</summary>
public static class ExtractionProviders
{
    public const string OpenAiCompatible = "OpenAiCompatible";

    public static readonly IReadOnlyList<string> Known = [OpenAiCompatible];
}

/// <summary>
/// Which book catalogue turns mention titles into real books. On by default with Open Library, which needs no local
/// setup; an empty <see cref="Provider"/> turns lookup off. Each provider reads its own settings from its own section.
/// </summary>
public sealed class BookLookupOptions : IValidatableObject
{
    public const string Section = "BookLookup";

    public string? Provider { get; set; } = BookLookupProviders.OpenLibrary;

    public bool Enabled => !string.IsNullOrWhiteSpace(Provider);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Enabled && !BookLookupProviders.Known.Contains(Provider!, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ValidationResult($"Unknown BookLookup:Provider '{Provider}'. Known: {string.Join(", ", BookLookupProviders.Known)}.");
        }
    }
}

/// <summary>Names accepted in <see cref="BookLookupOptions.Provider"/>, and the one stored on books no catalogue has.</summary>
public static class BookLookupProviders
{
    public const string OpenLibrary = "OpenLibrary";

    /// <summary>
    /// A book kept as the person typed it on the review page, because no catalogue has it. Stored as
    /// <see cref="Data.Book.Provider"/>, never chosen in config: it has no adapter.
    /// </summary>
    public const string Manual = "Manual";

    public static readonly IReadOnlyList<string> Known = [OpenLibrary];

    /// <summary>
    /// The host every book link from each catalogue is on. The adapters build these links themselves; a stored pick is
    /// checked against this again before it becomes a book.
    /// </summary>
    public static string? InfoHost(string provider) => provider switch
    {
        OpenLibrary => "openlibrary.org",
        _ => null,
    };
}

/// <summary>Outbound HTTP settings for feed-supplied URLs.</summary>
public sealed class HttpOptions
{
    public const string Section = "Http";

    /// <summary>
    /// Lets feeds, transcripts and audio be fetched from private, loopback and link-local addresses. Off by default:
    /// feed content is untrusted, and without this guard a feed could point the Worker at devices on your network.
    /// </summary>
    public bool AllowPrivateNetworkFeeds { get; set; }
}
