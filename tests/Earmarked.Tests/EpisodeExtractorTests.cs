using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Earmarked.Core.Backends;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Extraction;
using Earmarked.Core.Pipeline;
using Earmarked.Core.Transcripts;

namespace Earmarked.Tests;

/// <summary>Runs the extractor against a migrated SQLite file with a fake extraction backend.</summary>
public sealed class EpisodeExtractorTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("earmarked-extractor-").FullName;
    private readonly FakeExtractor _backend = new();
    private readonly EpisodeStateLock _lock = new();
    private IDbContextFactory<AppDbContext> _dbFactory = null!;
    private EpisodeExtractor _extractor = null!;
    private CommandProcessor _commands = null!;
    private Guid _episodeId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "test.db")}")
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        _dbFactory = new PooledDbContextFactory<AppDbContext>(options);
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.Database.MigrateAsync(Ct);
            var feed = new PodcastFeed { FeedUrl = "https://feeds.test/show.xml", Title = "A Show", AddedAt = DateTime.UtcNow };
            var episode = new Episode
            {
                PodcastFeed = feed,
                IdentityKey = "ep-1",
                Title = "Ines Calder on lighthouses",
                PublishedAt = DateTime.UtcNow.AddDays(-2),
                State = EpisodeState.Transcribed,
                Transcript = new Transcript
                {
                    Source = TranscriptSource.Transcribed,
                    Format = TranscriptFormat.SpeechToText,
                    Segments =
                    [
                        new(TimeSpan.FromSeconds(0), null, null, "Welcome back to the show."),
                        new(TimeSpan.FromSeconds(30), null, null, "I wrote The Lighthouse Keeper's Almanac after a winter on the coast."),
                        new(TimeSpan.FromSeconds(95), null, null, "And everyone should read Small Rivers, honestly, it's wonderful."),
                        new(TimeSpan.FromSeconds(160), null, null, "Thanks for listening, see you next week."),
                    ],
                },
            };
            db.Episodes.Add(episode);
            await db.SaveChangesAsync(Ct);
            _episodeId = episode.Id;
        }

        // Windows of 20 words with 8 overlapping: three requests for this 34-word transcript.
        _extractor = Extractor(waitForTranscription: false);
        _commands = new CommandProcessor(_dbFactory, null!, _lock,
            new TranscriptionWorkspace(Options.Create(new PathsOptions { Data = _root })), TimeProvider.System, NullLogger<CommandProcessor>.Instance);
    }

    [Fact]
    public async Task A_prioritised_episode_goes_first_and_its_priority_clears_when_done()
    {
        Guid newer;
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            var original = await db.Episodes.Include(e => e.Transcript).SingleAsync(e => e.Id == _episodeId, Ct);
            var episode = new Episode
            {
                PodcastFeedId = original.PodcastFeedId,
                IdentityKey = "ep-2",
                Title = "A newer episode",
                PublishedAt = DateTime.UtcNow,
                State = EpisodeState.Transcribed,
                Transcript = new Transcript
                {
                    Source = TranscriptSource.Transcribed,
                    Format = TranscriptFormat.SpeechToText,
                    Segments = [new(TimeSpan.Zero, null, null, "Nothing about books here.")],
                },
            };
            db.Episodes.Add(episode);
            original.PrioritisedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(Ct);
            newer = episode.Id;
        }

        Assert.Equal(WorkOutcome.Worked, await _extractor.ProcessNextAsync(Ct));

        await using var check = await _dbFactory.CreateDbContextAsync(Ct);
        var first = await check.Episodes.SingleAsync(e => e.Id == _episodeId, Ct);
        Assert.Equal((EpisodeState.Extracted, (DateTime?)null), (first.State, first.PrioritisedAt));
        Assert.Equal(EpisodeState.Transcribed, (await check.Episodes.SingleAsync(e => e.Id == newer, Ct)).State);
    }

    public ValueTask DisposeAsync()
    {
        _lock.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Stores_each_book_once_with_the_time_and_words_of_its_quote()
    {
        Assert.Equal(WorkOutcome.Worked, await _extractor.ProcessNextAsync(Ct));

        var episode = await LoadAsync();
        Assert.Equal(EpisodeState.Extracted, episode.State);
        Assert.Equal(["The Lighthouse Keeper's Almanac", "Small Rivers"], episode.Mentions.OrderBy(m => m.StartTime).Select(m => m.Title));

        var almanac = episode.Mentions.Single(m => m.Kind == MentionKind.OwnBook);
        Assert.Equal(TimeSpan.FromSeconds(30), almanac.StartTime);
        Assert.Equal("I wrote The Lighthouse Keeper's Almanac after a winter on the coast.", almanac.Quote);
        Assert.Equal(("fake", "fake-model", "fake-prompt-v1"), (almanac.Provider, almanac.Model, almanac.PromptVersion));
        Assert.Equal(episode.Transcript!.Id, almanac.TranscriptId);

        var rivers = episode.Mentions.Single(m => m.Title == "Small Rivers");
        Assert.Equal(TimeSpan.FromSeconds(95), rivers.StartTime);
        Assert.True(_backend.Calls.Count > 2);
        Assert.All(_backend.Calls, c => Assert.Equal(("A Show", "Ines Calder on lighthouses"), (c.PodcastTitle, c.EpisodeTitle)));
    }

    [Fact]
    public async Task Drops_mentions_whose_quote_is_not_in_the_transcript()
    {
        _backend.Extra = new ExtractedMention("The Silent Orchard", null, "I adored The Silent Orchard by the fire", MentionKind.Recommended, 1);

        await _extractor.ProcessNextAsync(Ct);

        Assert.DoesNotContain((await LoadAsync()).Mentions, m => m.Title == "The Silent Orchard");
    }

    [Fact]
    public async Task An_unreachable_backend_pauses_without_using_attempts()
    {
        _backend.Failure = () => new BackendUnavailableException("Can't reach the extraction backend.");

        Assert.Equal(WorkOutcome.Paused, await _extractor.ProcessNextAsync(Ct));

        var episode = await LoadAsync();
        Assert.Equal(EpisodeState.Extracting, episode.State);
        Assert.Equal(0, episode.Attempts);

        _backend.Failure = null;
        Assert.Equal(WorkOutcome.Worked, await _extractor.ProcessNextAsync(Ct));
        Assert.Equal(EpisodeState.Extracted, (await LoadAsync()).State);
    }

    [Fact]
    public async Task Gives_up_after_the_last_attempt_and_keeps_the_transcript()
    {
        _backend.Failure = () => new ExtractionFailedException("Extraction backend returned HTTP 500: boom");

        await _extractor.ProcessNextAsync(Ct);
        var first = await LoadAsync();
        Assert.Equal((EpisodeState.Extracting, 1), (first.State, first.Attempts));
        Assert.True(first.NextAttemptAt > DateTime.UtcNow);
        Assert.Equal(WorkOutcome.Idle, await _extractor.ProcessNextAsync(Ct));

        await SetAsync(e => e.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1));
        await _extractor.ProcessNextAsync(Ct);

        var failed = await LoadAsync();
        Assert.Equal(EpisodeState.ExtractionFailed, failed.State);
        Assert.StartsWith("Extraction gave up after 2 attempts", failed.LastError, StringComparison.Ordinal);
        Assert.NotNull(failed.Transcript);
    }

    [Theory]
    [InlineData(EpisodeState.New)]
    [InlineData(EpisodeState.AwaitingTranscription)]
    [InlineData(EpisodeState.Transcribing)]
    [InlineData(EpisodeState.Extracted)]
    [InlineData(EpisodeState.ExtractionFailed)]
    [InlineData(EpisodeState.Failed)]
    public async Task Only_claims_episodes_with_a_finished_transcript(EpisodeState state)
    {
        await SetAsync(e => e.State = state);

        Assert.Equal(WorkOutcome.Idle, await _extractor.ProcessNextAsync(Ct));
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task A_reset_during_extraction_discards_the_results()
    {
        _backend.OnCall = async call =>
        {
            if (call == 1)
            {
                await SetAsync(e => e.State = EpisodeState.Transcribed);
            }
        };

        await _extractor.ProcessNextAsync(Ct);

        var episode = await LoadAsync();
        Assert.Equal(EpisodeState.Transcribed, episode.State);
        Assert.Empty(episode.Mentions);
    }

    [Fact]
    public async Task Reextract_deletes_the_mentions_and_queues_the_episode_again()
    {
        await _extractor.ProcessNextAsync(Ct);
        await QueueAsync(WorkerCommandType.ReextractEpisode);

        await _commands.ProcessPendingAsync(Ct);

        var episode = await LoadAsync();
        Assert.Equal(EpisodeState.Transcribed, episode.State);
        Assert.Empty(episode.Mentions);
        Assert.NotNull(episode.Transcript);
        Assert.Equal(WorkOutcome.Worked, await _extractor.ProcessNextAsync(Ct));
        Assert.Equal(2, (await LoadAsync()).Mentions.Count);
    }

    [Fact]
    public async Task Reextract_is_refused_while_the_episode_is_being_extracted()
    {
        await SetAsync(e => e.State = EpisodeState.Extracting);
        await QueueAsync(WorkerCommandType.ReextractEpisode);

        await _commands.ProcessPendingAsync(Ct);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Contains("can't be re-extracted", (await db.WorkerCommands.SingleAsync(Ct)).Error, StringComparison.Ordinal);
        Assert.Equal(EpisodeState.Extracting, (await LoadAsync()).State);
    }

    [Fact]
    public async Task Resetting_an_episode_deletes_its_mentions_with_its_transcript()
    {
        await _extractor.ProcessNextAsync(Ct);
        await QueueAsync(WorkerCommandType.RetryEpisode);

        await _commands.ProcessPendingAsync(Ct);

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.Empty(await db.BookMentions.ToListAsync(Ct));
        Assert.Equal(EpisodeState.AwaitingTranscription, (await LoadAsync()).State);
    }

    [Fact]
    public async Task Waits_while_transcription_has_an_episode_due_and_a_healthy_backend()
    {
        var extractor = Extractor(waitForTranscription: true);
        await AddAwaitingAsync(publishesTranscripts: false);
        await SetTranscriptionHealthAsync(healthy: true);

        Assert.Equal(WorkOutcome.Idle, await extractor.ProcessNextAsync(Ct));
        Assert.Equal(EpisodeState.Transcribed, (await LoadAsync()).State);

        await SetTranscriptionHealthAsync(healthy: false);

        Assert.Equal(WorkOutcome.Worked, await extractor.ProcessNextAsync(Ct));
        Assert.Equal(EpisodeState.Extracted, (await LoadAsync()).State);
    }

    [Fact]
    public async Task Does_not_wait_when_the_option_is_off()
    {
        await AddAwaitingAsync(publishesTranscripts: false);
        await SetTranscriptionHealthAsync(healthy: true);

        Assert.Equal(WorkOutcome.Worked, await _extractor.ProcessNextAsync(Ct));
    }

    [Fact]
    public async Task Does_not_wait_for_an_episode_still_in_its_published_transcript_grace_period()
    {
        // The transcriber wouldn't take it yet either, so the GPU is free.
        await AddAwaitingAsync(publishesTranscripts: true);
        await SetTranscriptionHealthAsync(healthy: true);

        Assert.Equal(WorkOutcome.Worked, await Extractor(waitForTranscription: true).ProcessNextAsync(Ct));
    }

    [Fact]
    public async Task Does_not_wait_when_the_transcription_status_row_is_stale()
    {
        var extractor = Extractor(waitForTranscription: true);
        await AddAwaitingAsync(publishesTranscripts: false);
        await SetTranscriptionHealthAsync(healthy: true, updatedAt: DateTime.UtcNow - WorkerStatusNames.StaleAfter - TimeSpan.FromMinutes(1));

        Assert.Equal(WorkOutcome.Worked, await extractor.ProcessNextAsync(Ct));
    }

    [Fact]
    public async Task A_prioritised_episode_does_not_wait_for_transcription()
    {
        await AddAwaitingAsync(publishesTranscripts: false);
        await SetTranscriptionHealthAsync(healthy: true);
        await SetAsync(e => e.PrioritisedAt = DateTime.UtcNow);

        Assert.Equal(WorkOutcome.Worked, await Extractor(waitForTranscription: true).ProcessNextAsync(Ct));
    }

    [Fact]
    public async Task Does_not_wait_when_transcription_is_not_configured()
    {
        await AddAwaitingAsync(publishesTranscripts: false);
        await SetTranscriptionHealthAsync(healthy: true);

        Assert.Equal(WorkOutcome.Worked, await Extractor(waitForTranscription: true, transcriptionProvider: null).ProcessNextAsync(Ct));
    }

    private EpisodeExtractor Extractor(bool waitForTranscription, string? transcriptionProvider = "OpenAiCompatible") =>
        new(_dbFactory, _backend, _lock,
            Options.Create(new ExtractionOptions { Provider = "OpenAiCompatible", WindowWords = 20, OverlapWords = 8, WaitForTranscription = waitForTranscription }),
            Options.Create(new TranscriptionOptions { Provider = transcriptionProvider, PublishedTranscriptGraceDays = 3 }),
            Options.Create(new PollingOptions { MaxAttempts = 2 }),
            TimeProvider.System, NullLogger<EpisodeExtractor>.Instance);

    /// <summary>Adds an episode waiting for speech-to-text, published an hour ago, on a podcast of its own.</summary>
    private async Task AddAwaitingAsync(bool publishesTranscripts)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        db.Episodes.Add(new Episode
        {
            PodcastFeed = new PodcastFeed { FeedUrl = "https://feeds.test/other.xml", Title = "Another Show", AddedAt = DateTime.UtcNow, PublishesTranscripts = publishesTranscripts },
            IdentityKey = "waiting",
            Title = "Not transcribed yet",
            PublishedAt = DateTime.UtcNow.AddHours(-1),
            EnclosureUrl = "https://media.test/waiting.mp3",
            State = EpisodeState.AwaitingTranscription,
        });
        await db.SaveChangesAsync(Ct);
    }

    private async Task SetTranscriptionHealthAsync(bool healthy, DateTime? updatedAt = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var row = await db.WorkerStatuses.SingleOrDefaultAsync(s => s.Name == BackendHealth.Transcription, Ct);
        if (row is null)
        {
            row = new WorkerStatus { Name = BackendHealth.Transcription };
            db.WorkerStatuses.Add(row);
        }

        row.Healthy = healthy;
        row.UpdatedAt = updatedAt ?? DateTime.UtcNow;
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Episode> LoadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await db.Episodes.Include(e => e.Transcript).Include(e => e.Mentions).SingleAsync(e => e.Id == _episodeId, Ct);
    }

    private async Task SetAsync(Action<Episode> change)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        change(await db.Episodes.SingleAsync(e => e.Id == _episodeId, Ct));
        await db.SaveChangesAsync(Ct);
    }

    private async Task QueueAsync(WorkerCommandType type)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        db.WorkerCommands.Add(new WorkerCommand { Type = type, EpisodeId = _episodeId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Reports each book whose title appears in the window, with its sentence as the quote, so overlapping windows
    /// report the same book more than once. Can add an invented mention, fail, or run a hook on each call.
    /// </summary>
    private sealed class FakeExtractor : IBookExtractor
    {
        private static readonly (string Title, string Quote, MentionKind Kind)[] Books =
        [
            ("The Lighthouse Keeper's Almanac", "I wrote The Lighthouse Keeper's Almanac after a winter on the coast.", MentionKind.OwnBook),
            ("Small Rivers", "everyone should read Small Rivers, honestly,", MentionKind.Recommended),
        ];

        public string Provider => "fake";
        public string Model => "fake-model";
        public string PromptVersion => "fake-prompt-v1";
        public List<ExtractionInput> Calls { get; } = [];
        public ExtractedMention? Extra { get; set; }
        public Func<Exception>? Failure { get; set; }
        public Func<int, Task>? OnCall { get; set; }

        public async Task<IReadOnlyList<ExtractedMention>> ExtractAsync(ExtractionInput input, CancellationToken cancellationToken)
        {
            Calls.Add(input);
            if (OnCall is not null)
            {
                await OnCall(Calls.Count);
            }

            if (Failure is not null)
            {
                throw Failure();
            }

            var found = Books
                .Where(b => input.Text.Contains(b.Title.Split(' ')[^1], StringComparison.Ordinal))
                .Select(b => new ExtractedMention(b.Title, null, b.Quote, b.Kind, 0.9))
                .ToList();
            if (Extra is not null)
            {
                found.Add(Extra);
            }

            return found;
        }
    }
}
