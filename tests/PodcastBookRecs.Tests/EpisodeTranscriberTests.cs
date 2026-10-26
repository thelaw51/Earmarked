using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Audio;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Pipeline;
using PodcastBookRecs.Core.Transcription;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Tests;

/// <summary>Runs the transcriber against a migrated SQLite file, with fake chunking and a fake backend.</summary>
public sealed class EpisodeTranscriberTests : IAsyncLifetime
{
    private const string AudioUrl = "https://cdn.test/private/token-abc/episode.mp3";

    private readonly string _root = Directory.CreateTempSubdirectory("pbr-transcriber-").FullName;
    private readonly FakeTranscription _backend = new();
    private readonly AudioHost _audio = new();
    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private TranscriptionWorkspace _workspace = null!;
    private EpisodeTranscriber _transcriber = null!;
    private Guid _feedId;
    private Guid _episodeId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(_root, "test.db")}").Options;
        _dbFactory = new PooledDbContextFactory<AppDbContext>(options);
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.Database.MigrateAsync(Ct);
            var feed = new PodcastFeed { FeedUrl = "https://feeds.test/show.xml", AddedAt = DateTime.UtcNow, LastPolledAt = DateTime.UtcNow };
            var episode = new Episode
            {
                PodcastFeed = feed,
                IdentityKey = "ep-1",
                Title = "Episode 1",
                PublishedAt = DateTime.UtcNow.AddDays(-10),
                EnclosureUrl = AudioUrl,
                State = EpisodeState.AwaitingTranscription,
            };
            db.Episodes.Add(episode);
            await db.SaveChangesAsync(Ct);
            (_feedId, _episodeId) = (feed.Id, episode.Id);
        }

        _workspace = new TranscriptionWorkspace(Options.Create(new PathsOptions { Data = Path.Combine(_root, "data") }));
        _transcriber = new EpisodeTranscriber(
            _dbFactory, _audio, _backend, new FakeChunker(chunks: 3), new EpisodeStateLock(), _workspace,
            Options.Create(new TranscriptionOptions { Provider = "OpenAiCompatible", MinFreeDiskGb = 1, PublishedTranscriptGraceDays = 3 }),
            Options.Create(new PollingOptions { MaxAttempts = 2, IntervalMinutes = 60 }),
            TimeProvider.System, NullLogger<EpisodeTranscriber>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Transcribes_an_episode_and_shifts_each_chunk_by_its_offset()
    {
        Assert.Equal(WorkOutcome.Worked, await _transcriber.ProcessNextAsync(Ct));

        var episode = await LoadAsync();
        Assert.Equal(EpisodeState.Transcribed, episode.State);
        Assert.Equal(TranscriptSource.Transcribed, episode.Transcript!.Source);
        Assert.Equal(TranscriptFormat.SpeechToText, episode.Transcript.Format);
        Assert.Equal("fake", episode.Transcript.Provider);
        Assert.Equal("fake-model", episode.Transcript.Model);
        Assert.Equal([0, 60, 120], episode.Transcript.Segments.Select(s => (int)s.Start!.Value.TotalSeconds));
        Assert.Equal("chunk_002.wav", episode.Transcript.Segments[2].Text);
        Assert.False(Directory.Exists(_workspace.For(_episodeId)));
    }

    [Fact]
    public async Task An_unreachable_backend_pauses_without_using_attempts_and_resumes_where_it_stopped()
    {
        _backend.FailOnCall = 2;
        _backend.Failure = () => new BackendUnavailableException("Can't reach the transcription backend.");

        Assert.Equal(WorkOutcome.Paused, await _transcriber.ProcessNextAsync(Ct));
        var paused = await LoadAsync();
        Assert.Equal(EpisodeState.Transcribing, paused.State);
        Assert.Equal(0, paused.Attempts);
        Assert.True(File.Exists(Path.Combine(_workspace.For(_episodeId), "chunk_000.json")));

        _backend.FailOnCall = null;
        Assert.Equal(WorkOutcome.Worked, await _transcriber.ProcessNextAsync(Ct));

        Assert.Equal(EpisodeState.Transcribed, (await LoadAsync()).State);
        Assert.Equal(["chunk_000.wav", "chunk_001.wav", "chunk_001.wav", "chunk_002.wav"], _backend.Calls);
        Assert.Equal(1, _audio.Downloads);
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt_and_cleans_up()
    {
        _backend.FailOnCall = 1;
        _backend.Failure = () => new TranscriptionFailedException("Transcription backend returned HTTP 500: bad audio");

        Assert.Equal(WorkOutcome.Worked, await _transcriber.ProcessNextAsync(Ct));
        var first = await LoadAsync();
        Assert.Equal(EpisodeState.Transcribing, first.State);
        Assert.Equal(1, first.Attempts);
        Assert.True(first.NextAttemptAt > DateTime.UtcNow);
        Assert.Equal(WorkOutcome.Idle, await _transcriber.ProcessNextAsync(Ct));

        _backend.FailOnCall = _backend.Calls.Count + 1;
        await SetAsync(e => e.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1));
        await _transcriber.ProcessNextAsync(Ct);

        var failed = await LoadAsync();
        Assert.Equal(EpisodeState.Failed, failed.State);
        Assert.StartsWith("Gave up after 2 attempts", failed.LastError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_workspace.For(_episodeId)));
    }

    [Fact]
    public async Task An_expired_audio_link_waits_for_the_next_poll_and_never_shows_the_url()
    {
        _audio.Status = HttpStatusCode.Forbidden;

        await _transcriber.ProcessNextAsync(Ct);

        var episode = await LoadAsync();
        Assert.Equal(1, episode.Attempts);
        Assert.True(episode.NextAttemptAt >= episode.PodcastFeed.LastPolledAt!.Value.AddMinutes(60));
        Assert.Contains("may have expired", episode.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("token-abc", episode.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shows_that_publish_transcripts_get_a_grace_period()
    {
        await SetFeedAsync(f => f.PublishesTranscripts = true);
        await SetAsync(e => e.PublishedAt = DateTime.UtcNow.AddDays(-1));
        Assert.Equal(WorkOutcome.Idle, await _transcriber.ProcessNextAsync(Ct));

        await SetAsync(e => e.PublishedAt = DateTime.UtcNow.AddDays(-4));
        Assert.Equal(WorkOutcome.Worked, await _transcriber.ProcessNextAsync(Ct));
    }

    [Theory]
    [InlineData(EpisodeState.New)]
    [InlineData(EpisodeState.TranscriptFetched)]
    [InlineData(EpisodeState.Skipped)]
    [InlineData(EpisodeState.Failed)]
    public async Task Only_claims_episodes_waiting_for_transcription(EpisodeState state)
    {
        await SetAsync(e => e.State = state);

        Assert.Equal(WorkOutcome.Idle, await _transcriber.ProcessNextAsync(Ct));
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task A_reset_during_transcription_is_left_alone()
    {
        _backend.OnCall = async call =>
        {
            if (call == 2)
            {
                // What "Reset and retry" does: put the episode back and delete its work folder.
                await SetAsync(e => e.State = EpisodeState.AwaitingTranscription);
                _workspace.Delete(_episodeId);
            }
        };

        Assert.Equal(WorkOutcome.Worked, await _transcriber.ProcessNextAsync(Ct));

        var episode = await LoadAsync();
        Assert.Equal(EpisodeState.AwaitingTranscription, episode.State);
        Assert.Null(episode.Transcript);
        Assert.Equal(0, episode.Attempts);
    }

    [Fact]
    public async Task Deletes_work_folders_that_no_longer_belong_to_a_transcribing_episode()
    {
        var orphan = Guid.CreateVersion7();
        Directory.CreateDirectory(_workspace.For(orphan));
        Directory.CreateDirectory(_workspace.For(_episodeId));
        await SetAsync(e => e.State = EpisodeState.Transcribing);

        await _transcriber.CleanUpOrphansAsync(Ct);

        Assert.False(Directory.Exists(_workspace.For(orphan)));
        Assert.True(Directory.Exists(_workspace.For(_episodeId)));
    }

    private async Task<Episode> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await db.Episodes.Include(e => e.Transcript).Include(e => e.PodcastFeed).SingleAsync(e => e.Id == _episodeId, Ct);
    }

    private async Task SetAsync(Action<Episode> change)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        change(await db.Episodes.SingleAsync(e => e.Id == _episodeId, Ct));
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetFeedAsync(Action<PodcastFeed> change)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        change(await db.PodcastFeeds.SingleAsync(f => f.Id == _feedId, Ct));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Returns one segment per chunk at 0-5 s, whose text is the chunk's file name. Can fail on a chosen call.</summary>
    private sealed class FakeTranscription : ITranscriptionService
    {
        public string Provider => "fake";
        public string Model => "fake-model";
        public List<string> Calls { get; } = [];
        public int? FailOnCall { get; set; }
        public Func<Exception> Failure { get; set; } = () => new TranscriptionFailedException("failed");
        public Func<int, Task>? OnCall { get; set; }

        public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string fileName, CancellationToken cancellationToken)
        {
            Calls.Add(fileName);
            if (OnCall is not null)
            {
                await OnCall(Calls.Count);
            }

            if (FailOnCall == Calls.Count)
            {
                throw Failure();
            }

            return new TranscriptionResult([new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(5), null, fileName)], "en", null);
        }
    }

    /// <summary>Writes placeholder chunk files a minute apart, like ffmpeg would.</summary>
    private sealed class FakeChunker(int chunks) : IAudioChunker
    {
        public Task<IReadOnlyList<AudioChunk>> SplitAsync(string sourcePath, string outputDirectory, TimeSpan chunkLength, CancellationToken cancellationToken)
        {
            var list = Enumerable.Range(0, chunks).Select(i =>
            {
                var path = Path.Combine(outputDirectory, $"chunk_{i:000}.wav");
                if (!File.Exists(path))
                {
                    File.WriteAllBytes(path, [0]);
                }

                return new AudioChunk(i, path, TimeSpan.FromMinutes(i));
            }).ToList();
            return Task.FromResult<IReadOnlyList<AudioChunk>>(list);
        }
    }

    /// <summary>Serves a few bytes of "audio", or the configured error status.</summary>
    private sealed class AudioHost : HttpMessageHandler, IHttpClientFactory
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Downloads { get; private set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }
}
