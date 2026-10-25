using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Pipeline;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Tests;

/// <summary>Runs the poller and fetcher against a real, migrated SQLite file and a stubbed HTTP server.</summary>
public sealed class PipelineTests : IAsyncLifetime
{
    private const string FeedUrl = "https://example.com/feed.xml";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pbr-test-{Guid.NewGuid():N}.db");
    private readonly StubHandler _http = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private FeedPoller _poller = null!;
    private TranscriptFetcher _fetcher = null!;

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
            db.Podcasts.Add(new Podcast { FeedUrl = FeedUrl, AddedAt = DateTime.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        var polling = Options.Create(new PollingOptions { BackfillEpisodes = 2, MaxAttempts = 2 });
        var http = new StubHttpClientFactory(_http);
        _poller = new FeedPoller(_dbFactory, http, polling, TimeProvider.System, NullLogger<FeedPoller>.Instance);
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
        await _poller.PollAsync(1, Ct);

        var states = await EpisodeStatesAsync();
        Assert.Equal(EpisodeState.New, states["ep-003"]);
        Assert.Equal(EpisodeState.AwaitingTranscription, states["ep-002"]);
        Assert.Equal(EpisodeState.Skipped, states["https://cdn.example.com/ep1.mp3"]);

        const string cover = "<itunes:image href=\"https://example.com/cover.jpg\"/>";
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml").Replace(
            cover,
            cover + "<item><title>Episode 4</title><guid>ep-004</guid><pubDate>Mon, 26 Oct 2026 05:00:00 GMT</pubDate></item>",
            StringComparison.Ordinal));
        await _poller.PollAsync(1, Ct);

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
        await _poller.PollAsync(1, Ct);

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
    public async Task Falls_back_to_the_next_link_then_to_whisper_when_links_are_gone()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        _http.Respond("https://example.com/ep3.vtt", "", HttpStatusCode.NotFound);
        _http.Respond("https://example.com/ep3.html", Fixtures.Read("transcript.html"));
        await _poller.PollAsync(1, Ct);

        await _fetcher.ProcessNextAsync(Ct);
        Assert.Equal(EpisodeState.TranscriptFetched, (await EpisodeStatesAsync())["ep-003"]);
    }

    [Fact]
    public async Task Transient_errors_back_off_then_hand_over_to_whisper()
    {
        _http.Respond(FeedUrl, Fixtures.Read("feed.xml"));
        _http.Respond("https://example.com/ep3.vtt", "", HttpStatusCode.ServiceUnavailable);
        await _poller.PollAsync(1, Ct);

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
    public async Task Records_feed_errors_on_the_podcast()
    {
        _http.Respond(FeedUrl, "", HttpStatusCode.InternalServerError);
        await _poller.PollAsync(1, Ct);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var podcast = await db.Podcasts.SingleAsync(Ct);
        Assert.Contains("500", podcast.LastPollError, StringComparison.Ordinal);
        Assert.Null(podcast.FirstPolledAt);
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
