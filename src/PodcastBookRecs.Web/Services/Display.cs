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
        EpisodeState.Transcribed => "Transcribed",
        EpisodeState.Skipped => "Skipped (backfill)",
        EpisodeState.Failed => "Failed",
        _ => state.ToString(),
    };

    public static Color StateColor(EpisodeState state) => state switch
    {
        EpisodeState.New => Color.Info,
        EpisodeState.AwaitingTranscription => Color.Warning,
        EpisodeState.TranscriptFetched or EpisodeState.Transcribed => Color.Success,
        EpisodeState.Failed => Color.Error,
        _ => Color.Default,
    };
}
