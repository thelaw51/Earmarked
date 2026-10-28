using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Pipeline;
using Earmarked.Core.Transcripts;

namespace Earmarked.Tests;

/// <summary>Runs the poller and fetcher against a real, migrated SQLite file and a stubbed HTTP server.</summary>
public sealed class PipelineTests : IAsyncLifetime
{
    private const string FeedUrl = "https://example.com/feed.xml";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"earmarked-test-{Guid.NewGuid():N}.db");
    private readonly StubHandler _http = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid _podcastId;
    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private FeedPoller _poller = null!;
    private TranscriptFetcher _fetcher = null!;
    private CommandProcessor _commands = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        _dbFactory = new PooledDbContextFactory<AppDbContext>(options);
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.Database.MigrateAsync(Ct);
            var podcast = new PodcastFeed { FeedUrl = FeedUrl, AddedAt = DateTime.UtcNow };
            db.PodcastFeeds.Add(podcast);
            _podcastId = podcast.Id;
            await db.SaveChangesAsync(Ct);
        }

        var polling = Options.Create(new PollingOptions { BackfillEpisodes = 2, MaxAttempts = 2 });
        var http = new StubHttpClientFactory(_http);
        var stateLock = new EpisodeStateLock();
        _poller = new FeedPoller(_dbFactory, http, polling, stateLock, TimeProvider.System, NullLogger<FeedPoller>.Instance);
        _commands = new CommandProcessor(_dbFactory, _poller, stateLock,
            new TranscriptionWorkspace(Options.Create(new PathsOptions { Data = Path.GetTempPath() })), TimeProvider.System, NullLogger<CommandProcessor>.Instance);
        _fetcher = new TranscriptFetcher(_dbFactory, http, polling, TimeProvider.System, NullLogger<TranscriptFetcher>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task First_poll_applies_backfill_and_later_polls_add_only_new_items()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);

        var states = await EpisodeStatesAsync();
        Assert.Equal(EpisodeState.New, states["ep-003"]);
        Assert.Equal(EpisodeState.AwaitingTranscription, states["ep-002"]);
        Assert.Equal(EpisodeState.Skipped, states["https://cdn.example.com/ep1.mp3"]);

        const string cover = "<itunes:image href=\"https://example.com/cover.jpg\"/>";
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml").Replace(
            cover,
            cover + "<item><title>Episode 4</title><guid>ep-004</guid><pubDate>Mon, 26 Oct 2026 05:00:00 GMT</pubDate></item>",
            StringComparison.Ordinal));
        await _poller.PollAsync(_podcastId, Ct);

        states = await EpisodeStatesAsync();
        Assert.Equal(4, states.Count);
        Assert.Equal(EpisodeState.AwaitingTranscription, states["ep-004"]);
        Assert.Equal(EpisodeState.Skipped, states["https://cdn.example.com/ep1.mp3"]);
    }

    [Fact]
    public async Task Fetches_the_preferred_transcript_and_stores_its_segments()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        _http.Respond("https://example.com/ep3.vtt", Fixtures.Read("transcript.vtt"));
        await _poller.PollAsync(_podcastId, Ct);

        Assert.True(await _fetcher.ProcessNextAsync(Ct));
        Assert.False(await _fetcher.ProcessNextAsync(Ct));

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var episode = await db.Episodes.Include(e => e.Transcript).SingleAsync(e => e.IdentityKey == "ep-003", Ct);
        Assert.Equal(EpisodeState.TranscriptFetched, episode.State);
        Assert.Equal(TranscriptFormat.Vtt, episode.Transcript!.Format);
        Assert.Equal(3, episode.Transcript.Segments.Count);
        Assert.Equal("Bob Smith", episode.Transcript.Segments[1].Speaker);
        Assert.DoesNotContain("https://example.com/ep3.html", _http.Requested);
    }

    [Fact]
    public async Task Falls_back_to_the_next_link_then_to_transcription_when_links_are_gone()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        _http.Respond("https://example.com/ep3.vtt", "", HttpStatusCode.NotFound);
        _http.Respond("https://example.com/ep3.html", Fixtures.Read("transcript.html"));
        await _poller.PollAsync(_podcastId, Ct);

        await _fetcher.ProcessNextAsync(Ct);
        Assert.Equal(EpisodeState.TranscriptFetched, (await EpisodeStatesAsync())["ep-003"]);
    }

    [Fact]
    public async Task Transient_errors_back_off_then_hand_over_to_transcription()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        _http.Respond("https://example.com/ep3.vtt", "", HttpStatusCode.ServiceUnavailable);
        await _poller.PollAsync(_podcastId, Ct);

        await _fetcher.ProcessNextAsync(Ct);
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            var episode = await db.Episodes.SingleAsync(e => e.IdentityKey == "ep-003", Ct);
            Assert.Equal(EpisodeState.New, episode.State);
            Assert.Equal(1, episode.Attempts);
            Assert.True(episode.NextAttemptAt > DateTime.UtcNow);

            // Pretend the backoff has passed.
            episode.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync(Ct);
        }

        await _fetcher.ProcessNextAsync(Ct);
        Assert.Equal(EpisodeState.AwaitingTranscription, (await EpisodeStatesAsync())["ep-003"]);
    }

    [Fact]
    public async Task A_transcript_published_after_release_is_picked_up_on_a_later_poll()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);
        Assert.Equal(EpisodeState.AwaitingTranscription, (await EpisodeStatesAsync())["ep-002"]);

        _http.Respond(FeedUrl, Fixtures.Read("feed.xml").Replace(
            "<guid isPermaLink=\"false\">ep-002</guid>",
            "<guid isPermaLink=\"false\">ep-002</guid><podcast:transcript url=\"https://example.com/ep2.srt\" type=\"application/x-subrip\"/>",
            StringComparison.Ordinal));
        _http.Respond("https://example.com/ep2.srt", Fixtures.Read("transcript.srt"));
        await _poller.PollAsync(_podcastId, Ct);
        Assert.Equal(EpisodeState.New, (await EpisodeStatesAsync())["ep-002"]);

        while (await _fetcher.ProcessNextAsync(Ct))
        {
        }

        Assert.Equal(EpisodeState.TranscriptFetched, (await EpisodeStatesAsync())["ep-002"]);
    }

    [Fact]
    public async Task Links_that_already_failed_are_not_retried_on_every_poll()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);
        await _fetcher.ProcessNextAsync(Ct);
        Assert.Equal(EpisodeState.AwaitingTranscription, (await EpisodeStatesAsync())["ep-003"]);

        await _poller.PollAsync(_podcastId, Ct);

        Assert.Equal(EpisodeState.AwaitingTranscription, (await EpisodeStatesAsync())["ep-003"]);
        Assert.False(await _fetcher.ProcessNextAsync(Ct));
    }

    [Fact]
    public async Task Marks_feeds_that_publish_transcripts()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.True((await db.PodcastFeeds.SingleAsync(Ct)).PublishesTranscripts);
    }

    [Fact]
    public async Task Later_polls_refresh_the_audio_url_of_waiting_episodes()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);

        _http.Respond(FeedUrl, Fixtures.Read("feed.xml").Replace("https://cdn.example.com/ep2.mp3", "https://cdn.example.com/ep2.mp3?token=new", StringComparison.Ordinal));
        await _poller.PollAsync(_podcastId, Ct);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var episode = await db.Episodes.SingleAsync(e => e.IdentityKey == "ep-002", Ct);
        Assert.Equal("https://cdn.example.com/ep2.mp3?token=new", episode.EnclosureUrl);
        Assert.Equal(EpisodeState.AwaitingTranscription, episode.State);
    }

    [Fact]
    public async Task Records_feed_errors_on_the_podcast()
    {
        _http.Respond(FeedUrl, "", HttpStatusCode.InternalServerError);
        await _poller.PollAsync(_podcastId, Ct);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var podcast = await db.PodcastFeeds.SingleAsync(Ct);
        Assert.Contains("500", podcast.LastPollError, StringComparison.Ordinal);
        Assert.Null(podcast.FirstPolledAt);
    }

    [Fact]
    public async Task Including_a_skipped_episode_queues_it_and_polls_its_feed_for_fresh_links()
    {
        const string skipped = "https://cdn.example.com/ep1.mp3";
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);
        var polledBefore = await LastPolledAsync();

        var error = await RunCommandAsync(WorkerCommandType.IncludeEpisode, skipped);

        Assert.Null(error);
        var episode = await EpisodeAsync(skipped);
        Assert.Equal((EpisodeState.AwaitingTranscription, (DateTime?)null), (episode.State, episode.PrioritisedAt));
        Assert.True(await LastPolledAsync() > polledBefore);
    }

    [Fact]
    public async Task Do_this_next_on_a_skipped_episode_includes_it_and_prioritises_it()
    {
        const string skipped = "https://cdn.example.com/ep1.mp3";
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);

        Assert.Null(await RunCommandAsync(WorkerCommandType.PrioritiseEpisode, skipped));

        var episode = await EpisodeAsync(skipped);
        Assert.Equal(EpisodeState.AwaitingTranscription, episode.State);
        Assert.NotNull(episode.PrioritisedAt);
    }

    [Fact]
    public async Task Do_this_next_clears_a_retry_backoff_and_is_refused_once_an_episode_is_done()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.Episodes.Where(e => e.IdentityKey == "ep-002").ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, DateTime.UtcNow.AddHours(1)), Ct);
            await db.Episodes.Where(e => e.IdentityKey == "ep-003").ExecuteUpdateAsync(s => s.SetProperty(e => e.State, EpisodeState.Extracted), Ct);
        }

        Assert.Null(await RunCommandAsync(WorkerCommandType.PrioritiseEpisode, "ep-002"));
        Assert.Null((await EpisodeAsync("ep-002")).NextAttemptAt);
        Assert.Contains("can't be prioritised", await RunCommandAsync(WorkerCommandType.PrioritiseEpisode, "ep-003"), StringComparison.Ordinal);
        Assert.Contains("isn't skipped", await RunCommandAsync(WorkerCommandType.IncludeEpisode, "ep-002"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Including_skipped_episodes_queues_the_newest_ones_and_polls_the_feed_once()
    {
        const string cover = "<itunes:image href=\"https://example.com/cover.jpg\"/>";
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml").Replace(
            cover,
            cover
            + "<item><title>Episode 0</title><guid>ep-000</guid><pubDate>Mon, 12 Oct 2026 05:00:00 GMT</pubDate></item>"
            + "<item><title>Episode -1</title><guid>ep-minus-1</guid><pubDate>Mon, 05 Oct 2026 05:00:00 GMT</pubDate></item>",
            StringComparison.Ordinal));
        await _poller.PollAsync(_podcastId, Ct);
        var polledBefore = await LastPolledAsync();

        Assert.Null(await RunPodcastCommandAsync(WorkerCommandType.IncludeSkippedEpisodes, 2));

        var states = await EpisodeStatesAsync();
        Assert.Equal(EpisodeState.AwaitingTranscription, states["https://cdn.example.com/ep1.mp3"]);
        Assert.Equal(EpisodeState.AwaitingTranscription, states["ep-000"]);
        Assert.Equal(EpisodeState.Skipped, states["ep-minus-1"]);
        Assert.True(await LastPolledAsync() > polledBefore);
    }

    [Fact]
    public async Task Including_skipped_episodes_is_refused_when_none_are_left_or_the_count_is_out_of_range()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);

        Assert.Contains("missing its target", await RunPodcastCommandAsync(WorkerCommandType.IncludeSkippedEpisodes, 0), StringComparison.Ordinal);
        Assert.Contains("missing its target", await RunPodcastCommandAsync(WorkerCommandType.IncludeSkippedEpisodes, CommandProcessor.MaxIncludeCount + 1), StringComparison.Ordinal);
        Assert.Null(await RunPodcastCommandAsync(WorkerCommandType.IncludeSkippedEpisodes, 50));
        Assert.Contains("no skipped episodes", await RunPodcastCommandAsync(WorkerCommandType.IncludeSkippedEpisodes, 1), StringComparison.Ordinal);
    }

    /// <summary>Queues a command for the episode with this identity key, runs it, and returns its error.</summary>
    [Fact]
    public async Task Without_extraction_priority_clears_once_an_episode_has_its_transcript()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        await _poller.PollAsync(_podcastId, Ct);
        EpisodeState[] states = [EpisodeState.Transcribed, EpisodeState.TranscriptFetched, EpisodeState.AwaitingTranscription];
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            var episodes = await db.Episodes.OrderBy(e => e.IdentityKey).ToListAsync(Ct);
            Assert.True(episodes.Count >= states.Length);
            for (var i = 0; i < episodes.Count; i++)
            {
                episodes[i].State = states[Math.Min(i, states.Length - 1)];
                episodes[i].PrioritisedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(Ct);
        }

        await new PriorityRelease(_dbFactory, new EpisodeStateLock()).ReleaseAsync(Ct);

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        var left = await check.Episodes.Select(e => new { e.State, Prioritised = e.PrioritisedAt != null }).ToListAsync(Ct);
        // Only the episode still waiting for a transcript keeps its place at the front.
        Assert.All(left, e => Assert.Equal(e.State == EpisodeState.AwaitingTranscription, e.Prioritised));
    }

    private async Task<string?> RunCommandAsync(WorkerCommandType type, string identityKey)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            var id = await db.Episodes.Where(e => e.IdentityKey == identityKey).Select(e => e.Id).SingleAsync(Ct);
            db.WorkerCommands.Add(new WorkerCommand { Type = type, EpisodeId = id, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        await _commands.ProcessPendingAsync(Ct);
        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        return (await check.WorkerCommands.OrderByDescending(c => c.CreatedAt).FirstAsync(Ct)).Error;
    }

    /// <summary>Queues a command for the test podcast with this count, runs it, and returns its error.</summary>
    private async Task<string?> RunPodcastCommandAsync(WorkerCommandType type, int count)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            db.WorkerCommands.Add(new WorkerCommand { Type = type, PodcastFeedId = _podcastId, Count = count, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        await _commands.ProcessPendingAsync(Ct);
        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        return (await check.WorkerCommands.OrderByDescending(c => c.CreatedAt).FirstAsync(Ct)).Error;
    }

    private async Task<DateTime?> LastPolledAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await db.PodcastFeeds.Where(p => p.Id == _podcastId).Select(p => p.LastPolledAt).SingleAsync(Ct);
    }

    private async Task<Episode> EpisodeAsync(string identityKey)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await db.Episodes.SingleAsync(e => e.IdentityKey == identityKey, Ct);
    }

    private async Task<Dictionary<string, EpisodeState>> EpisodeStatesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await db.Episodes.ToDictionaryAsync(e => e.IdentityKey, e => e.State, Ct);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = [];

        public List<string> Requested { get; } = [];

        public void Respond(string url, string body, HttpStatusCode status = HttpStatusCode.OK) => _responses[url] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var (status, body) = _responses.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
