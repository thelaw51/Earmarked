using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Earmarked.Core.Data;
using Earmarked.Core.Text;

namespace Earmarked.Core.Feeds;

/// <summary>A parsed RSS podcast feed.</summary>
public sealed record ParsedFeed(string? Title, string? Author, string? ImageUrl, IReadOnlyList<ParsedEpisode> Episodes);

/// <summary>One feed item with the fields the pipeline needs.</summary>
public sealed record ParsedEpisode(
    string IdentityKey,
    string? ItemGuid,
    string Title,
    DateTime? PublishedAt,
    TimeSpan? Duration,
    string? EnclosureUrl,
    string? EnclosureType,
    long? EnclosureLength,
    string? Link,
    IReadOnlyList<TranscriptLink> TranscriptLinks,
    string? Description = null);

/// <summary>
/// Parses RSS 2.0 podcast feeds, including the iTunes and Podcasting 2.0 namespaces.
/// Items with neither a GUID nor an enclosure URL are dropped because nothing identifies them across polls. Show notes
/// come from <c>content:encoded</c>, which usually has the full notes, then <c>description</c>, then
/// <c>itunes:summary</c>, reduced to plain text.
/// </summary>
public static class FeedParser
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Podcast = "https://podcastindex.org/namespace/1.0";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        Async = true,
    };

    public static async Task<ParsedFeed> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var reader = XmlReader.Create(stream, ReaderSettings);
        var doc = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        return Parse(doc);
    }

    public static ParsedFeed Parse(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
        return Parse(XDocument.Load(reader));
    }

    private static ParsedFeed Parse(XDocument doc)
    {
        var channel = doc.Root?.Name.LocalName == "rss" ? doc.Root.Element("channel") : null;
        if (channel is null)
        {
            throw new FeedFormatException("Not an RSS feed: no <rss><channel> element.");
        }

        var image = channel.Element(Itunes + "image")?.Attribute("href")?.Value
            ?? channel.Element("image")?.Element("url")?.Value;

        var episodes = new List<ParsedEpisode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in channel.Elements("item"))
        {
            var episode = ParseItem(item);
            if (episode is not null && seen.Add(episode.IdentityKey))
            {
                episodes.Add(episode);
            }
        }

        return new ParsedFeed(
            Clean(channel.Element("title")?.Value),
            Clean(channel.Element(Itunes + "author")?.Value),
            Clean(image),
            episodes);
    }

    private static ParsedEpisode? ParseItem(XElement item)
    {
        var guid = Clean(item.Element("guid")?.Value);
        var enclosure = item.Element("enclosure");
        var enclosureUrl = Clean(enclosure?.Attribute("url")?.Value);
        var identity = guid ?? enclosureUrl;
        if (identity is null)
        {
            return null;
        }

        var transcripts = item.Elements(Podcast + "transcript")
            .Select(t => (Url: Clean(t.Attribute("url")?.Value), Element: t))
            .Where(t => t.Url is not null)
            .Select(t => new TranscriptLink
            {
                Url = t.Url!,
                Type = Clean(t.Element.Attribute("type")?.Value),
                Language = Clean(t.Element.Attribute("language")?.Value),
                Rel = Clean(t.Element.Attribute("rel")?.Value),
            })
            .ToList();

        return new ParsedEpisode(
            identity,
            guid,
            Clean(item.Element("title")?.Value) ?? Clean(item.Element(Itunes + "title")?.Value) ?? "(untitled)",
            RssDate.TryParse(item.Element("pubDate")?.Value),
            ParseDuration(item.Element(Itunes + "duration")?.Value),
            enclosureUrl,
            Clean(enclosure?.Attribute("type")?.Value),
            long.TryParse(enclosure?.Attribute("length")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length > 0 ? length : null,
            Clean(item.Element("link")?.Value),
            transcripts,
            PlainText.FromHtml(Clean(item.Element(Content + "encoded")?.Value) ?? Clean(item.Element("description")?.Value) ?? item.Element(Itunes + "summary")?.Value,
                Episode.MaxDescriptionLength));
    }

    /// <summary>Parses <c>itunes:duration</c>, which is either whole seconds or <c>[h:]mm:ss</c>.</summary>
    internal static TimeSpan? ParseDuration(string? value)
    {
        value = Clean(value);
        if (value is null)
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
        }

        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return null;
        }

        var numbers = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return numbers.Length == 3
            ? new TimeSpan(numbers[0], numbers[1], numbers[2])
            : new TimeSpan(0, numbers[0], numbers[1]);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Thrown when a document isn't a feed this parser understands.</summary>
public sealed class FeedFormatException(string message) : Exception(message);
