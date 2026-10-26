using System.Globalization;
using MudBlazor;
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

    /// <summary>A short note on a mention that isn't linked to a book, or null when it is.</summary>
    public static string? MatchNote(MatchState state, bool lookupOn) => state switch
    {
        MatchState.Pending => lookupOn ? "Checking the catalogue" : "Book lookup is off",
        MatchState.NoMatch => "Not found in the catalogue",
        MatchState.LookupFailed => "Catalogue lookup failed",
        _ => null,
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
