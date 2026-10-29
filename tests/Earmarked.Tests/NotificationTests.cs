using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Earmarked.Core.Backends;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Notifications;
using Earmarked.Core.Providers.Notifications.Ntfy;
using Earmarked.Core.Transcripts;

namespace Earmarked.Tests;

/// <summary>The outbox, dispatcher, backend alerts and daily summary against a migrated SQLite file, with a fake sender.</summary>
public sealed class NotificationTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("earmarked-notifications-").FullName;
    private readonly ManualTime _time = new();
    private readonly FakeSender _sender = new();
    private readonly NotificationsOptions _options = new() { Provider = "Ntfy", BaseUrl = "https://ntfy.example.com", Topic = "earmarked-test", AppUrl = "https://earmarked.example.com" };
    private IDbContextFactory<AppDbContext> _dbFactory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "test.db")}")
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        _dbFactory = new PooledDbContextFactory<AppDbContext>(options);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        await db.Database.MigrateAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Sends_pending_notifications_oldest_first_and_marks_them_sent()
    {
        await AddAsync("first");
        _time.Advance(TimeSpan.FromSeconds(1));
        await AddAsync("second");

        await Dispatcher().SendPendingAsync(Ct);

        Assert.Equal(["first", "second"], _sender.Sent.Select(m => m.Title));
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.All(await db.Notifications.ToListAsync(Ct), n => Assert.Equal((NotificationState.Sent, (DateTime?)_time.Now), (n.State, n.SentAt)));
    }

    [Fact]
    public async Task An_unreachable_server_keeps_everything_waiting_without_counting_attempts()
    {
        await AddAsync("first");
        await AddAsync("second");
        _sender.Failure = () => new BackendUnavailableException("Can't reach ntfy.");

        await Dispatcher().SendPendingAsync(Ct);

        Assert.Equal(1, _sender.Calls);
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            Assert.All(await db.Notifications.ToListAsync(Ct), n => Assert.Equal((NotificationState.Pending, 0), (n.State, n.Attempts)));
        }

        _sender.Failure = null;
        await Dispatcher().SendPendingAsync(Ct);
        Assert.Equal(2, _sender.Sent.Count);
    }

    [Fact]
    public async Task A_refused_notification_backs_off_and_is_dropped_after_the_last_attempt()
    {
        await AddAsync("refused");
        _sender.Failure = () => new NotificationRejectedException("ntfy refused the message with HTTP 403.");
        var dispatcher = Dispatcher();

        await dispatcher.SendPendingAsync(Ct);
        await dispatcher.SendPendingAsync(Ct);
        Assert.Equal(1, _sender.Calls);

        for (var i = 1; i < NotificationDispatcher.MaxAttempts; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(40));
            await dispatcher.SendPendingAsync(Ct);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var notice = await db.Notifications.SingleAsync(Ct);
        Assert.Equal((NotificationState.Dropped, NotificationDispatcher.MaxAttempts), (notice.State, notice.Attempts));
    }

    [Fact]
    public async Task Drops_notifications_that_waited_more_than_a_day_and_forgets_old_ones()
    {
        await AddAsync("stale");
        _time.Advance(NotificationDispatcher.MaxAge + TimeSpan.FromMinutes(1));
        await AddAsync("fresh");

        await Dispatcher().SendPendingAsync(Ct);

        Assert.Equal(["fresh"], _sender.Sent.Select(m => m.Title));
        _time.Advance(NotificationDispatcher.KeepFor + TimeSpan.FromMinutes(1));
        await Dispatcher().SendPendingAsync(Ct);
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        Assert.False(await db.Notifications.AnyAsync(Ct));
    }

    [Fact]
    public async Task Reports_a_backend_only_after_it_stays_down_and_its_recovery_once()
    {
        var alerts = Alerts();

        await alerts.PublishAsync(Report(("transcription", false, "Unreachable: connection refused.")), Ct);
        _time.Advance(TimeSpan.FromMinutes(4));
        await alerts.PublishAsync(Report(("transcription", false, "Unreachable: connection refused.")), Ct);
        Assert.Empty(await NoticesAsync());

        _time.Advance(TimeSpan.FromMinutes(1));
        await alerts.PublishAsync(Report(("transcription", false, "Unreachable: connection refused.")), Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        await alerts.PublishAsync(Report(("transcription", false, "Unreachable: connection refused.")), Ct);
        var down = Assert.Single(await NoticesAsync());
        Assert.Equal((NotificationKind.BackendDown, "Problem with transcription", "Unreachable: connection refused."), (down.Kind, down.Title, down.Body));
        Assert.Equal("https://earmarked.example.com/status", down.ClickUrl);

        _time.Advance(TimeSpan.FromMinutes(54));
        await alerts.PublishAsync(Report(("transcription", true, null)), Ct);
        await alerts.PublishAsync(Report(("transcription", true, null)), Ct);
        var notices = await NoticesAsync();
        Assert.Equal(2, notices.Count);
        Assert.Equal(("Transcription is back", "Healthy again after 1 hour."), (notices[1].Title, notices[1].Body));
    }

    [Fact]
    public async Task A_short_blip_and_the_notification_server_itself_stay_quiet()
    {
        var alerts = Alerts();

        await alerts.PublishAsync(Report(("extraction", false, "Unreachable."), (BackendHealth.Notifications, false, "Unreachable.")), Ct);
        _time.Advance(TimeSpan.FromMinutes(2));
        await alerts.PublishAsync(Report(("extraction", true, null), (BackendHealth.Notifications, false, "Unreachable.")), Ct);
        _time.Advance(TimeSpan.FromMinutes(10));
        await alerts.PublishAsync(Report(("extraction", true, null), (BackendHealth.Notifications, false, "Unreachable.")), Ct);

        Assert.Empty(await NoticesAsync());
    }

    [Fact]
    public async Task The_daily_summary_waits_for_its_time_then_goes_once_a_day()
    {
        _time.Set(new DateTime(2026, 10, 1, 20, 59, 0, DateTimeKind.Utc));
        await SeedDayAsync(_time.Now.AddHours(-3));
        var summary = Summary();

        await summary.RunAsync(Ct);
        Assert.Empty(await NoticesAsync());

        _time.Advance(TimeSpan.FromMinutes(1));
        await summary.RunAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        await summary.RunAsync(Ct);

        var notice = Assert.Single(await NoticesAsync());
        Assert.Equal((NotificationKind.DailySummary, "daily-summary:2026-10-01"), (notice.Kind, notice.DedupeKey));
        Assert.Equal("Today: 2 books from 1 episode", notice.Title);
        Assert.Equal(
            "Episodes searched for books: 1\nBooks found: 2 (0 matched to a catalogue so far)\nTranscripts: 1 transcribed, 0 published by the podcast",
            notice.Body);

        _time.Advance(TimeSpan.FromDays(1));
        await summary.RunAsync(Ct);
        var notices = await NoticesAsync();
        Assert.Equal(2, notices.Count);
        Assert.Equal("Today: 0 books from 0 episodes", notices[1].Title);
    }

    [Fact]
    public async Task The_daily_summary_counts_from_the_previous_one()
    {
        _time.Set(new DateTime(2026, 10, 1, 21, 0, 0, DateTimeKind.Utc));
        await Summary().RunAsync(Ct);

        // The Worker was off at 21:00 the next day and started at 23:00: this summary covers 26 hours.
        _time.Set(new DateTime(2026, 10, 2, 23, 0, 0, DateTimeKind.Utc));
        await SeedDayAsync(new DateTime(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc));
        await Summary().RunAsync(Ct);

        Assert.Equal("Today: 2 books from 1 episode", (await NoticesAsync())[1].Title);
    }

    [Fact]
    public void Outbox_text_drops_control_characters_and_lists_at_most_five_titles()
    {
        var outbox = new NotificationOutbox(Options.Create(_options), _time);
        using var db = _dbFactory.CreateDbContext();

        outbox.EpisodeExtracted(db, Guid.NewGuid(), "A\u0007Show", "Ep\r\n1", [.. Enumerable.Range(1, 7).Select(i => $"Book {i}")]);

        var notice = db.ChangeTracker.Entries<Notification>().Single().Entity;
        Assert.Equal("7 books found", notice.Title);
        Assert.Equal("Ep \n1 (A Show)\n- Book 1\n- Book 2\n- Book 3\n- Book 4\n- Book 5\nand 2 more", notice.Body);
    }

    [Theory]
    [InlineData(1, "under a minute")]
    [InlineData(61, "1 minute")]
    [InlineData(3_900, "1 hour 5 minutes")]
    [InlineData(7_200, "2 hours")]
    public void Describes_an_outage_length(int seconds, string expected) =>
        Assert.Equal(expected, NotificationOutbox.Describe(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public async Task Ntfy_publishes_json_to_the_root_with_a_bearer_token()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var sender = Ntfy(handler, new NotificationsOptions { Topic = "earmarked-test", Token = "tk_example" });

        await sender.SendAsync(new NotificationMessage(NotificationKind.BackendDown, "Problem with transcription", "Unreachable.\nSecond line", "https://earmarked.example.com/status"), Ct);

        Assert.Equal((HttpMethod.Post, "https://ntfy.example.com/"), (handler.Method, handler.Url));
        Assert.Equal("Bearer tk_example", handler.Authorization);
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("earmarked-test", (string?)body["topic"]);
        Assert.Equal("Problem with transcription", (string?)body["title"]);
        Assert.Equal("Unreachable.\nSecond line", (string?)body["message"]);
        Assert.Equal(4, (int?)body["priority"]);
        Assert.Equal("rotating_light", (string?)body["tags"]![0]);
        Assert.Equal("https://earmarked.example.com/status", (string?)body["click"]);
        Assert.Null(body["markdown"]);
    }

    [Fact]
    public async Task Ntfy_uses_basic_auth_for_a_user_and_none_without_credentials()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);

        await Ntfy(handler, new NotificationsOptions { Topic = "t", Username = "phone", Password = "pa:ss" }).SendAsync(Message(), Ct);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("phone:pa:ss")), handler.Authorization);

        await Ntfy(handler, new NotificationsOptions { Topic = "t" }).SendAsync(Message(), Ct);
        Assert.Null(handler.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Ntfy_rate_limits_and_server_errors_mean_wait(HttpStatusCode status) =>
        await Assert.ThrowsAsync<BackendUnavailableException>(() => Ntfy(new RecordingHandler(status), new NotificationsOptions { Topic = "t" }).SendAsync(Message(), Ct));

    [Fact]
    public async Task Ntfy_refusing_a_message_shows_on_its_status_row_until_one_is_accepted()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden);
        var factory = new SingleClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://ntfy.example.com/") });
        var sender = new NtfyNotificationSender(factory, Options.Create(new NotificationsOptions { Topic = "t", Token = "tk_example" }));
        var check = new NtfyHealthCheck(factory, sender);

        var refused = await Assert.ThrowsAsync<NotificationRejectedException>(() => sender.SendAsync(Message(), Ct));
        Assert.DoesNotContain("tk_example", refused.Message, StringComparison.Ordinal);
        var unhealthy = await check.CheckHealthAsync(new HealthCheckContext(), Ct);
        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.Contains("HTTP 403", unhealthy.Description, StringComparison.Ordinal);

        handler.Status = HttpStatusCode.OK;
        await sender.SendAsync(Message(), Ct);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status);
        Assert.Equal("https://ntfy.example.com/v1/health", handler.Url);
    }

    [Theory]
    [InlineData("Topic", "has spaces")]
    [InlineData("Topic", null)]
    [InlineData("BaseUrl", "ftp://ntfy.example.com")]
    [InlineData("Token", "tk with space")]
    [InlineData("Username", "phone")]
    [InlineData("AppUrl", "javascript:alert(1)")]
    [InlineData("DailySummaryTime", "9pm")]
    [InlineData("Provider", "Pushover")]
    public void Rejects_bad_settings(string property, string? value)
    {
        var options = new NotificationsOptions { Provider = "Ntfy", BaseUrl = "https://ntfy.example.com", Topic = "earmarked" };
        typeof(NotificationsOptions).GetProperty(property)!.SetValue(options, value);

        Assert.NotEmpty(options.Validate(new ValidationContext(options)));
    }

    [Fact]
    public void Accepts_good_settings_and_ignores_everything_when_off()
    {
        var on = new NotificationsOptions { Provider = "ntfy", BaseUrl = "https://ntfy.example.com", Topic = "earmarked_1", Token = "tk_abc", DailySummaryTime = "07:30" };
        Assert.Empty(on.Validate(new ValidationContext(on)));
        Assert.Equal(new TimeOnly(7, 30), on.SummaryTime);

        var off = new NotificationsOptions { Topic = "not valid" };
        Assert.Empty(off.Validate(new ValidationContext(off)));
    }

    private NotificationDispatcher Dispatcher() => new(_dbFactory, _sender, _time, NullLogger<NotificationDispatcher>.Instance);

    private BackendAlertPublisher Alerts() => new(_dbFactory, new NotificationOutbox(Options.Create(_options), _time), Options.Create(_options), _time);

    private DailySummary Summary() =>
        new(_dbFactory, new NotificationOutbox(Options.Create(_options), _time), Options.Create(_options), _time, NullLogger<DailySummary>.Instance);

    private static NtfyNotificationSender Ntfy(HttpMessageHandler handler, NotificationsOptions options) =>
        new(new SingleClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://ntfy.example.com/") }), Options.Create(options));

    private static NotificationMessage Message() => new(NotificationKind.DailySummary, "Today", "Nothing much.", null);

    private static HealthReport Report(params (string Name, bool Healthy, string? Detail)[] entries) => new(
        entries.ToDictionary(e => e.Name, e => new HealthReportEntry(e.Healthy ? HealthStatus.Healthy : HealthStatus.Unhealthy, e.Detail, TimeSpan.Zero, null, null)),
        TimeSpan.Zero);

    private async Task AddAsync(string title)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        db.Notifications.Add(new Notification { Kind = NotificationKind.EpisodeExtracted, Title = title, Body = "body", CreatedAt = _time.Now });
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<Notification>> NoticesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        return await db.Notifications.OrderBy(n => n.CreatedAt).ToListAsync(Ct);
    }

    /// <summary>One transcribed episode, extracted at <paramref name="at"/> with two mentions.</summary>
    private async Task SeedDayAsync(DateTime at)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(Ct);
        var transcript = new Transcript { Source = TranscriptSource.Transcribed, Format = TranscriptFormat.SpeechToText, CreatedAt = at };
        var episode = new Episode
        {
            PodcastFeed = new PodcastFeed { FeedUrl = "https://feeds.test/show.xml", Title = "A Show", AddedAt = at },
            IdentityKey = "ep-1",
            Title = "An episode",
            State = EpisodeState.Extracted,
            UpdatedAt = at,
            Transcript = transcript,
        };
        db.Episodes.Add(episode);
        foreach (var title in new[] { "Small Rivers", "The Lighthouse Keeper's Almanac" })
        {
            db.BookMentions.Add(new BookMention
            {
                Episode = episode,
                Transcript = transcript,
                Title = title,
                Quote = title,
                Provider = "fake",
                Model = "fake",
                PromptVersion = "v1",
                CreatedAt = at,
            });
        }

        await db.SaveChangesAsync(Ct);
    }

    private sealed class FakeSender : INotificationSender
    {
        public List<NotificationMessage> Sent { get; } = [];
        public int Calls { get; private set; }
        public Func<Exception>? Failure { get; set; }

        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null)
            {
                throw Failure();
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public HttpMethod? Method { get; private set; }
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (Method, Url, Authorization) = (request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString());
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent("{}") };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>A clock the test moves by hand, in UTC so "local" times in the summary are predictable.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        public DateTime Now => _now.UtcDateTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan by) => _now += by;

        public void Set(DateTime utc) => _now = new DateTimeOffset(utc);
    }
}
