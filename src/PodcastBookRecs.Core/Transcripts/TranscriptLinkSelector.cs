using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Transcripts;

/// <summary>Works out each transcript link's format and picks the one with the best timing.</summary>
public static class TranscriptLinkSelector
{
    /// <summary>Detects the format from the MIME type, falling back to the URL's file extension when the type is missing or unknown.</summary>
    public static TranscriptFormat DetectFormat(string? mimeType, string url)
    {
        var fromType = mimeType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "text/vtt" => TranscriptFormat.Vtt,
            "application/x-subrip" or "application/srt" or "text/srt" => TranscriptFormat.Srt,
            "application/json" => TranscriptFormat.Json,
            "text/html" => TranscriptFormat.Html,
            "text/plain" => TranscriptFormat.PlainText,
            _ => TranscriptFormat.Unknown,
        };
        if (fromType != TranscriptFormat.Unknown)
        {
            return fromType;
        }

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".vtt" => TranscriptFormat.Vtt,
            ".srt" => TranscriptFormat.Srt,
            ".json" => TranscriptFormat.Json,
            ".html" or ".htm" => TranscriptFormat.Html,
            ".txt" => TranscriptFormat.PlainText,
            _ => TranscriptFormat.Unknown,
        };
    }

    /// <summary>Returns the preferred link (VTT, then SRT, JSON, HTML, plain text), or null if none has a known format.</summary>
    public static (TranscriptLink Link, TranscriptFormat Format)? SelectBest(IEnumerable<TranscriptLink> links) =>
        links
            .Select(l => (Link: l, Format: DetectFormat(l.Type, l.Url)))
            .Where(x => x.Format != TranscriptFormat.Unknown)
            .OrderBy(x => x.Format)
            .Cast<(TranscriptLink, TranscriptFormat)?>()
            .FirstOrDefault();
}
