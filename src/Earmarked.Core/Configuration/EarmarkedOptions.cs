using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Earmarked.Core.Configuration;

/// <summary>Where state lives. <c>/data</c> must be local disk, not a network share: SQLite locking breaks over SMB.</summary>
public sealed class PathsOptions
{
    public const string Section = "Paths";

    [Required]
    public string Data { get; set; } = "/data";

    /// <summary>
    /// The SQLite file. An install from before the rename keeps using its <c>podcastbookrecs.db</c>
    /// until that file is renamed, so upgrading never starts an empty database beside the real one.
    /// </summary>
    public string DatabaseFile
    {
        get
        {
            var current = Path.Combine(Data, "earmarked.db");
            var legacy = Path.Combine(Data, "podcastbookrecs.db");
            return !File.Exists(current) && File.Exists(legacy) ? legacy : current;
        }
    }
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

    /// <summary>
    /// Holds extraction back while transcription has work to do, so the two models don't share one GPU's memory.
    /// Only while transcription is configured and its Status row is healthy; "Do this next" episodes don't wait.
    /// </summary>
    public bool WaitForTranscription { get; set; }

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
/// Which book catalogues turn mention titles into real books. On by default with Open Library, which needs no local
/// setup; an empty <see cref="Provider"/> turns lookup off. <see cref="Fallbacks"/> are asked, in order, only for
/// titles the primary catalogue can't place. Each provider reads its own settings from its own section.
/// </summary>
public sealed class BookLookupOptions : IValidatableObject
{
    public const string Section = "BookLookup";

    public string? Provider { get; set; } = BookLookupProviders.OpenLibrary;

    /// <summary>Catalogues to try after <see cref="Provider"/>, in order. Empty by default.</summary>
    public string[] Fallbacks { get; set; } = [];

    public bool Enabled => !string.IsNullOrWhiteSpace(Provider);

    /// <summary>The primary catalogue and then the fallbacks, each in its canonical spelling.</summary>
    public IReadOnlyList<string> Chain => [.. Names.Select(BookLookupProviders.Canonical)];

    /// <summary>The primary and the fallbacks as config spells them.</summary>
    private IEnumerable<string> Names => new[] { Provider! }.Concat(Fallbacks);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        foreach (var name in Names)
        {
            if (!BookLookupProviders.Known.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                yield return new ValidationResult($"Unknown book catalogue '{name}' in BookLookup. Known: {string.Join(", ", BookLookupProviders.Known)}.");
            }
        }

        if (Names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Fallbacks.Length + 1)
        {
            yield return new ValidationResult("BookLookup:Fallbacks must not repeat a catalogue or name the primary BookLookup:Provider.");
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

    public const string AppleBooks = "AppleBooks";

    public const string Hardcover = "Hardcover";

    /// <summary>Catalogues that have an adapter. <see cref="Manual"/> isn't one.</summary>
    public static readonly IReadOnlyList<string> Known = [OpenLibrary, AppleBooks, Hardcover];

    /// <summary>
    /// The host every book link from each catalogue is on. The adapters build these links themselves; a stored pick is
    /// checked against this again before it becomes a book.
    /// </summary>
    public static string? InfoHost(string provider) => provider switch
    {
        OpenLibrary => "openlibrary.org",
        AppleBooks => "books.apple.com",
        Hardcover => "hardcover.app",
        _ => null,
    };

    /// <summary>
    /// The host each catalogue's covers come from; a subdomain of it also counts, since Apple spreads covers over
    /// several (<c>is1-ssl.mzstatic.com</c> and so on).
    /// </summary>
    public static string? CoverHost(string provider) => provider switch
    {
        OpenLibrary => "covers.openlibrary.org",
        AppleBooks => "mzstatic.com",
        Hardcover => "assets.hardcover.app",
        _ => null,
    };

    /// <summary>The name to show people, e.g. "Apple Books".</summary>
    public static string Label(string provider) => provider switch
    {
        OpenLibrary => "Open Library",
        AppleBooks => "Apple Books",
        Hardcover => "Hardcover",
        Manual => "No catalogue",
        _ => provider,
    };

    /// <summary>The known name spelled as stored, for a name config gave in any case.</summary>
    public static string Canonical(string name) => Known.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
}

/// <summary>
/// Push notifications to a phone. Off unless <see cref="Provider"/> is set. Each kind of message has its own switch;
/// all of them are on once a provider is set, except notices for episodes where no books were found.
/// </summary>
public sealed partial class NotificationsOptions : IValidatableObject
{
    public const string Section = "Notifications";

    /// <summary>Which adapter to use, e.g. <c>Ntfy</c>. Empty disables notifications.</summary>
    public string? Provider { get; set; }

    /// <summary>The notification server, e.g. <c>https://ntfy.example.com</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Where messages are published. On a public server the topic name is the only thing keeping it private.</summary>
    public string? Topic { get; set; }

    /// <summary>An access token, sent as a bearer token. Set it as <c>EARMARKED_Notifications__Token</c>, never in config.json.</summary>
    public string? Token { get; set; }

    /// <summary>For a server set up with user accounts instead of tokens. Ignored when <see cref="Token"/> is set.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>The address the web app is reached at, e.g. <c>https://earmarked.example.com</c>. Tapping a notification opens the page it's about.</summary>
    public string? AppUrl { get; set; }

    /// <summary>A notice each time an episode's transcript has been searched for books.</summary>
    public bool EpisodeExtracted { get; set; } = true;

    /// <summary>Also send <see cref="EpisodeExtracted"/> notices for episodes with no books in them.</summary>
    public bool EpisodesWithoutBooks { get; set; }

    /// <summary>A notice when an episode runs out of transcription or extraction attempts.</summary>
    public bool EpisodeFailures { get; set; } = true;

    /// <summary>A notice when a backend (the Mac, LM Studio, a book catalogue) is down, and again when it's back.</summary>
    public bool BackendAlerts { get; set; } = true;

    /// <summary>How long a backend must stay unhealthy before it's reported, so a short blip or a sleeping Mac stays quiet.</summary>
    [Range(0, 1440)]
    public int BackendAlertAfterMinutes { get; set; } = 5;

    /// <summary>One message a day counting what the pipeline did since the last one.</summary>
    public bool DailySummary { get; set; } = true;

    /// <summary>When the summary goes out, as <c>HH:mm</c> in the container's time zone (its <c>TZ</c>).</summary>
    public string DailySummaryTime { get; set; } = "21:00";

    public bool Enabled => !string.IsNullOrWhiteSpace(Provider);

    /// <summary><see cref="DailySummaryTime"/> parsed. Validation guarantees it parses whenever notifications are on.</summary>
    public TimeOnly SummaryTime => TimeOnly.TryParseExact(DailySummaryTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new TimeOnly(21, 0);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (!NotificationProviders.Known.Contains(Provider!, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ValidationResult($"Unknown Notifications:Provider '{Provider}'. Known: {string.Join(", ", NotificationProviders.Known)}.");
        }

        if (!IsHttpUrl(BaseUrl))
        {
            yield return new ValidationResult("Notifications:BaseUrl must be an absolute http(s) URL when a provider is set.");
        }

        if (Topic is null || !TopicPattern().IsMatch(Topic))
        {
            yield return new ValidationResult("Notifications:Topic is needed: 1 to 64 letters, digits, '-' or '_'.");
        }

        if (!string.IsNullOrEmpty(Token) && !CredentialPattern().IsMatch(Token))
        {
            yield return new ValidationResult("Notifications:Token must be printable characters with no spaces.");
        }

        if (string.IsNullOrEmpty(Token) && (string.IsNullOrEmpty(Username) != string.IsNullOrEmpty(Password)))
        {
            yield return new ValidationResult("Notifications:Username and Notifications:Password must be set together.");
        }

        if (!string.IsNullOrEmpty(Username) && Username.Contains(':', StringComparison.Ordinal))
        {
            yield return new ValidationResult("Notifications:Username must not contain ':'.");
        }

        if (!string.IsNullOrEmpty(AppUrl) && (!IsHttpUrl(AppUrl) || AppUrl.Length > 300))
        {
            yield return new ValidationResult("Notifications:AppUrl must be an absolute http(s) URL of at most 300 characters.");
        }

        if (!TimeOnly.TryParseExact(DailySummaryTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            yield return new ValidationResult("Notifications:DailySummaryTime must be a 24-hour time such as 21:00.");
        }
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex("^[-_A-Za-z0-9]{1,64}$")]
    private static partial Regex TopicPattern();

    [GeneratedRegex(@"^[\x21-\x7E]{1,200}$")]
    private static partial Regex CredentialPattern();
}

/// <summary>Names accepted in <see cref="NotificationsOptions.Provider"/>.</summary>
public static class NotificationProviders
{
    public const string Ntfy = "Ntfy";

    public static readonly IReadOnlyList<string> Known = [Ntfy];
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
