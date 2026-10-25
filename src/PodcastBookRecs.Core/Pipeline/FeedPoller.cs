using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Feeds;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>Fetches one podcast's feed and stores any new episodes.</summary>
public sealed partial class FeedPoller(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    IOptions<PollingOptions> polling,
    TimeProvider time,
    ILogger<FeedPoller> logger)
{
    private const long MaxFeedBytes = 64 * 1024 * 1024;

    public async Task PollAsync(int podcastId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var podcast = await db.Podcasts.SingleOrDefaultAsync(p => p.Id == podcastId, cancellationToken);
        if (podcast is null)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var feedName = podcast.Title ?? FeedUrlRedactor.Redact(podcast.FeedUrl);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, podcast.FeedUrl);
            if (podcast.ETag is not null && EntityTagHeaderValue.TryParse(podcast.ETag, out var etag))
            {
                request.Headers.IfNoneMatch.Add(etag);
            }

            if (podcast.LastModified is not null && DateTimeOffset.TryParse(podcast.LastModified, out var modified))
            {
                request.Headers.IfModifiedSince = modified;
            }

            using var response = await httpFactory.CreateClient(ServiceCollectionExtensions.FeedHttpClient)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotModified && podcast.FirstPolledAt is not null)
            {
                podcast.LastPolledAt = now;
                podcast.LastPollError = null;
                await db.SaveChangesAsync(cancellationToken);
                LogNotModified(feedName);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Feed returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            if (response.Content.Headers.ContentLength > MaxFeedBytes)
            {
                throw new FeedFormatException($"Feed is larger than {MaxFeedBytes / 1024 / 1024} MB.");
            }

            // Content-Length can be absent (chunked responses), so cap the bytes actually read as well.
            await response.Content.LoadIntoBufferAsync(MaxFeedBytes, cancellationToken);

            ParsedFeed feed;
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                feed = await FeedParser.ParseAsync(stream, cancellationToken);
            }

            var existing = (await db.Episodes.Where(e => e.PodcastId == podcast.Id).Select(e => e.IdentityKey).ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);
            var plan = EpisodeIntake.Plan(feed.Episodes, existing, podcast.FirstPolledAt is null, polling.Value.BackfillEpisodes);

            foreach (var (item, state) in plan)
            {
                db.Episodes.Add(new Episode
                {
                    PodcastId = podcast.Id,
                    IdentityKey = item.IdentityKey,
                    ItemGuid = item.ItemGuid,
                    Title = item.Title,
                    PublishedAt = item.PublishedAt,
                    Duration = item.Duration,
                    EnclosureUrl = item.EnclosureUrl,
                    EnclosureType = item.EnclosureType,
                    EnclosureLength = item.EnclosureLength,
                    Link = item.Link,
                    TranscriptLinks = [.. item.TranscriptLinks],
                    State = state,
                    DiscoveredAt = now,
                    UpdatedAt = now,
                });
            }

            podcast.Title = feed.Title ?? podcast.Title;
            podcast.Author = feed.Author ?? podcast.Author;
            podcast.ImageUrl = feed.ImageUrl ?? podcast.ImageUrl;
            podcast.ETag = response.Headers.ETag?.ToString();
            podcast.LastModified = response.Content.Headers.LastModified?.ToString("R");
            podcast.FirstPolledAt ??= now;
            podcast.LastPolledAt = now;
            podcast.LastPollError = null;
            await db.SaveChangesAsync(cancellationToken);

            var skipped = plan.Count(p => p.State == EpisodeState.Skipped);
            LogPolled(podcast.Title ?? feedName, plan.Count - skipped, skipped);
        }
        catch (Exception ex) when (ex is HttpRequestException or FeedFormatException or System.Xml.XmlException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            podcast.LastPolledAt = now;
            podcast.LastPollError = ex is TaskCanceledException ? "Timed out fetching the feed." : ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            LogPollFailed(feedName, podcast.LastPollError);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Polled {Feed}: {Queued} new episodes queued, {Skipped} skipped by the backfill window")]
    private partial void LogPolled(string feed, int queued, int skipped);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Feed {Feed} not modified")]
    private partial void LogNotModified(string feed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling {Feed} failed: {Error}")]
    private partial void LogPollFailed(string feed, string error);
}
