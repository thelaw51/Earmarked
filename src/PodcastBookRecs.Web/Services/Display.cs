using System.Globalization;
using MudBlazor;
using PodcastBookRecs.Core.Books;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Web.Services;

/// <summary>Formatting shared by the pages. Times are stored in UTC and shown in the container's TZ.</summary>
public static class Display
{
    public static string LocalTime(DateTime? utc) =>
        utc is null ? "—" : TimeZoneInfo.ConvertTimeFromUtc(utc.Value, TimeZoneInfo.Local).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string LocalDate(DateTime? utc) =>
        utc is null ? "—" : TimeZoneInfo.ConvertTimeFromUtc(utc.Value, TimeZoneInfo.Local).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Timestamp(TimeSpan? value) =>
        value is null ? "" : value.Value.TotalHours >= 1 ? value.Value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : value.Value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public static string StateLabel(EpisodeState state) => state switch
    {
        EpisodeState.New => "Transcript queued",
        EpisodeState.AwaitingTranscription => "Awaiting transcription",
        EpisodeState.TranscriptFetched => "Transcript fetched",
        EpisodeState.Transcribing => "Transcribing",
        EpisodeState.Transcribed => "Transcribed",
        EpisodeState.Extracting => "Finding books",
        EpisodeState.Extracted => "Books extracted",
        EpisodeState.Skipped => "Skipped (backfill)",
        EpisodeState.Failed => "Failed",
        EpisodeState.ExtractionFailed => "Book extraction failed",
        _ => state.ToString(),
    };

    /// <summary>Where a transcript came from, e.g. "Vtt from the feed" or "speech-to-text (OpenAiCompatible · whisper)".</summary>
    public static string TranscriptOrigin(Transcript transcript) =>
        transcript.Source == TranscriptSource.Transcribed
            ? $"speech-to-text ({transcript.Provider ?? "unknown provider"} · {transcript.Model ?? "unknown model"})"
            : $"{transcript.Format} from the feed";

    /// <summary>The URL if it is an absolute https link, otherwise null. For links and images whose URL came from outside.</summary>
    public static string? HttpsOrNull(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;

    /// <summary>
    /// The URL if it is an absolute http or https link, otherwise null. For feed artwork, which is often plain http;
    /// anything else (<c>data:</c>, <c>javascript:</c>, relative paths) is dropped.
    /// </summary>
    public static string? HttpOrHttpsOrNull(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? url : null;

    /// <summary>A short note on a mention that isn't linked to a book, or null when it is.</summary>
    public static string? MatchNote(MatchState state, bool lookupOn) => state switch
    {
        MatchState.Pending => lookupOn ? "Checking the catalogue" : "Book lookup is off",
        MatchState.NoMatch => "Not found in the catalogue",
        MatchState.LookupFailed => "Catalogue lookup failed",
        MatchState.Rejected => "Ruled out in review",
        _ => null,
    };

    private static readonly (ReviewReasons Reason, string Label)[] ReasonText =
    [
        (ReviewReasons.NotFound, "Not in the catalogue"),
        (ReviewReasons.LookupFailed, "Lookup failed"),
        (ReviewReasons.LowConfidence, "Model unsure it's a book"),
        (ReviewReasons.NoAuthor, "No author to check"),
        (ReviewReasons.FuzzySurname, "Author's name differs"),
        (ReviewReasons.FewEditions, "Few editions"),
        (ReviewReasons.OtherCatalogue, "Found in a fallback catalogue"),
    ];

    /// <summary>Why a title is in the review list, one short phrase per reason.</summary>
    public static IEnumerable<string> ReasonLabels(ReviewReasons reasons) => ReasonText.Where(r => reasons.HasFlag(r.Reason)).Select(r => r.Label);

    public static string VerdictLabel(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Confirmed => "Confirmed",
        ReviewVerdict.Corrected => "Corrected",
        ReviewVerdict.NotABook => "Not a book",
        ReviewVerdict.KeptAsIs => "Kept as is",
        _ => "Wrong book",
    };

    public static Color VerdictColor(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Confirmed or ReviewVerdict.Corrected or ReviewVerdict.KeptAsIs => Color.Success,
        _ => Color.Warning,
    };

    public static string KindLabel(MentionKind kind) => kind switch
    {
        MentionKind.OwnBook => "Own book",
        MentionKind.Recommended => "Recommended",
        MentionKind.Criticised => "Criticised",
        MentionKind.Discussed => "Discussed",
        _ => "Passing",
    };

    public static Color KindColor(MentionKind kind) => kind switch
    {
        MentionKind.Recommended => Color.Success,
        MentionKind.OwnBook => Color.Info,
        MentionKind.Criticised => Color.Warning,
        _ => Color.Default,
    };

    /// <summary>True while the Worker is actively working on the episode, as opposed to waiting in a queue.</summary>
    public static bool IsBusy(EpisodeState state) => state is EpisodeState.Transcribing or EpisodeState.Extracting;

    /// <summary>
    /// How far an episode has got through the three pipeline stages (transcript, books, done), from 0 to 3, and whether
    /// the current stage failed. Skipped episodes haven't started. With extraction off, a transcript is the end.
    /// </summary>
    public static (int Completed, bool Failed) Progress(EpisodeState state, bool extractionOn) => state switch
    {
        EpisodeState.TranscriptFetched or EpisodeState.Transcribed when !extractionOn => (3, false),
        EpisodeState.TranscriptFetched or EpisodeState.Transcribed or EpisodeState.Extracting => (1, false),
        EpisodeState.Extracted => (3, false),
        EpisodeState.Failed => (0, true),
        EpisodeState.ExtractionFailed => (1, true),
        _ => (0, false),
    };

    public static Color StateColor(EpisodeState state) => state switch
    {
        EpisodeState.New or EpisodeState.Transcribing or EpisodeState.Extracting => Color.Info,
        EpisodeState.AwaitingTranscription => Color.Warning,
        EpisodeState.TranscriptFetched or EpisodeState.Transcribed => Color.Primary,
        EpisodeState.Extracted => Color.Success,
        EpisodeState.Failed or EpisodeState.ExtractionFailed => Color.Error,
        _ => Color.Default,
    };
}
