using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Earmarked.Core.Audio;
using Earmarked.Core.Backends;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Data;
using Earmarked.Core.Extraction;
using Earmarked.Core.Net;
using Earmarked.Core.Notifications;
using Earmarked.Core.Pipeline;
using Earmarked.Core.Providers.BookLookup;
using Earmarked.Core.Providers.BookLookup.AppleBooks;
using Earmarked.Core.Providers.BookLookup.Hardcover;
using Earmarked.Core.Providers.BookLookup.OpenLibrary;
using Earmarked.Core.Providers.Extraction.OpenAiCompatible;
using Earmarked.Core.Providers.Notifications.Ntfy;
using Earmarked.Core.Providers.Transcription.OpenAiCompatible;
using Earmarked.Core.Transcription;

namespace Earmarked.Core.Configuration;

/// <summary>Configuration and service registration shared by the Worker and the UI.</summary>
public static class ServiceCollectionExtensions
{
    public const string EnvironmentPrefix = "EARMARKED_";

    /// <summary>The prefix from before the rename, still read so an existing stack keeps its settings.</summary>
    public const string LegacyEnvironmentPrefix = "PBR_";
    public const string FeedHttpClient = "feeds";
    public const string EnclosureHttpClient = "enclosures";

    private static readonly string UserAgent = $"Earmarked/{AppVersion.Display} (+https://github.com/thelaw51/Earmarked)";

    /// <summary>
    /// Adds <c>config.json</c> from the config directory (<c>/config</c> unless <c>EARMARKED_CONFIG_DIR</c> says otherwise),
    /// then <c>EARMARKED_</c> environment variables on top, e.g. <c>EARMARKED_Polling__IntervalMinutes=30</c>.
    /// Variables with the old <c>PBR_</c> prefix still apply, below <c>EARMARKED_</c> ones.
    /// </summary>
    public static IConfigurationBuilder AddEarmarkedSources(this IConfigurationBuilder configuration)
    {
        var configDir = Environment.GetEnvironmentVariable(EnvironmentPrefix + "CONFIG_DIR")
            ?? Environment.GetEnvironmentVariable(LegacyEnvironmentPrefix + "CONFIG_DIR")
            ?? "/config";
        return configuration
            .AddJsonFile(Path.Combine(Path.GetFullPath(configDir), "config.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(LegacyEnvironmentPrefix)
            .AddEnvironmentVariables(EnvironmentPrefix);
    }

    /// <summary>Registers options, the SQLite context factory, the feed and audio HTTP clients and the database health check.</summary>
    public static IServiceCollection AddEarmarkedCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PathsOptions>().Bind(configuration.GetSection(PathsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PollingOptions>().Bind(configuration.GetSection(PollingOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<HttpOptions>().Bind(configuration.GetSection(HttpOptions.Section));
        services.AddSingleton<EpisodeStateLock>();
        services.AddSingleton<TranscriptionWorkspace>();

        services.AddDbContextFactory<AppDbContext>((sp, options) =>
        {
            var paths = sp.GetRequiredService<IOptions<PathsOptions>>().Value;
            options.UseSqlite($"Data Source={paths.DatabaseFile}");
            options.AddInterceptors(new SqlitePragmaInterceptor());
        });

        services.AddHttpClient(FeedHttpClient, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(2);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(sp => CreateFeedHandler(sp.GetRequiredService<IOptions<HttpOptions>>().Value));

        // Episode audio: same guard as feeds, but large files, so no overall client timeout (the caller sets its own).
        services.AddHttpClient(EnclosureHttpClient, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(sp => CreateFeedHandler(sp.GetRequiredService<IOptions<HttpOptions>>().Value));

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
        return services;
    }

    /// <summary>
    /// Registers speech-to-text for the Worker: the configured provider's adapter and health check, the ffmpeg
    /// chunker and the transcriber. Does nothing beyond binding options when no provider is configured.
    /// </summary>
    public static IServiceCollection AddEarmarkedTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TranscriptionOptions>().Bind(configuration.GetSection(TranscriptionOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        var transcription = configuration.GetSection(TranscriptionOptions.Section).Get<TranscriptionOptions>() ?? new TranscriptionOptions();
        if (!transcription.Enabled)
        {
            return services;
        }

        services.AddHttpClient(OpenAiCompatibleTranscriptionService.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(transcription.BaseUrl!.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromMinutes(transcription.RequestTimeoutMinutes);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = OutboundConnections.ConnectWithTimeout(TimeSpan.FromSeconds(10)),
            });

        switch (transcription.Provider)
        {
            case var p when string.Equals(p, TranscriptionProviders.OpenAiCompatible, StringComparison.OrdinalIgnoreCase):
                // One instance for both, so the health check sees the state the transcriber's requests left.
                services.AddSingleton<OpenAiCompatibleTranscriptionService>();
                services.AddSingleton<ITranscriptionService>(sp => sp.GetRequiredService<OpenAiCompatibleTranscriptionService>());
                services.AddHealthChecks().AddCheck<OpenAiCompatibleHealthCheck>(BackendHealth.Transcription, tags: [BackendHealth.Tag], timeout: BackendHealth.Timeout);
                break;
        }

        services.AddSingleton<IAudioChunker, FfmpegAudioChunker>();
        services.AddSingleton<EpisodeTranscriber>();
        return services;
    }

    /// <summary>
    /// Registers book mention extraction for the Worker: the configured provider's adapter and health check, and the
    /// extractor. Does nothing beyond binding options when no provider is configured.
    /// </summary>
    public static IServiceCollection AddEarmarkedExtraction(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ExtractionOptions>().Bind(configuration.GetSection(ExtractionOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        var extraction = configuration.GetSection(ExtractionOptions.Section).Get<ExtractionOptions>() ?? new ExtractionOptions();
        if (!extraction.Enabled)
        {
            return services;
        }

        services.AddHttpClient(OpenAiCompatibleBookExtractor.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(extraction.BaseUrl!.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromMinutes(extraction.RequestTimeoutMinutes);
                client.MaxResponseContentBufferSize = 1024 * 1024;
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = OutboundConnections.ConnectWithTimeout(TimeSpan.FromSeconds(10)),
            });

        switch (extraction.Provider)
        {
            case var p when string.Equals(p, ExtractionProviders.OpenAiCompatible, StringComparison.OrdinalIgnoreCase):
                services.AddSingleton<IBookExtractor, OpenAiCompatibleBookExtractor>();
                services.AddHealthChecks().AddCheck<OpenAiCompatibleExtractionHealthCheck>(BackendHealth.Extraction, tags: [BackendHealth.Tag], timeout: BackendHealth.Timeout);
                break;
        }

        services.AddSingleton<EpisodeExtractor>();
        return services;
    }

    /// <summary>
    /// Registers book lookup for the Worker: each configured catalogue's adapter (keyed by its provider name) and health
    /// check, the chain that orders them, the mention matcher and the runner for review-page searches. Does nothing
    /// beyond binding options when lookup is turned off.
    /// </summary>
    public static IServiceCollection AddEarmarkedBookLookup(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BookLookupOptions>().Bind(configuration.GetSection(BookLookupOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        var lookup = configuration.GetSection(BookLookupOptions.Section).Get<BookLookupOptions>() ?? new BookLookupOptions();
        if (!lookup.Enabled)
        {
            return services;
        }

        var chain = lookup.Chain;
        foreach (var name in chain.Distinct())
        {
            var healthName = name == chain[0] ? BackendHealth.BookLookup : BackendHealth.BookLookupFallback(BookLookupProviders.Label(name));
            switch (name)
            {
                case BookLookupProviders.OpenLibrary:
                    AddOpenLibrary(services, configuration);
                    AddCatalogHealthCheck(services, name, OpenLibraryCatalog.HttpClientName, healthName);
                    break;
                case BookLookupProviders.AppleBooks:
                    AddAppleBooks(services, configuration);
                    AddCatalogHealthCheck(services, name, AppleBooksCatalog.HttpClientName, healthName, AppleBooksCatalog.SlowDownStatuses);
                    break;
                case BookLookupProviders.Hardcover:
                    AddHardcover(services, configuration);
                    // Its own check, because a plain GET can't tell a rejected token. A singleton keeps its memory of the last answer.
                    services.AddSingleton<HardcoverHealthCheck>();
                    services.AddHealthChecks().AddCheck<HardcoverHealthCheck>(healthName, tags: [BackendHealth.Tag], timeout: BackendHealth.Timeout);
                    break;
            }
        }

        // Unknown names fail options validation at start-up, so every name here has an adapter.
        services.AddSingleton(sp => new BookCatalogChain([.. chain.Select(sp.GetRequiredKeyedService<IBookCatalog>)]));
        services.AddSingleton<MentionMatcher>();
        services.AddSingleton<BookDetailsFetcher>();
        services.AddSingleton<CatalogSearchRunner>();
        return services;
    }

    /// <summary>
    /// Registers push notifications for the Worker: the configured provider's adapter and health check, the outbox the
    /// pipeline writes to, the dispatcher and daily summary the Worker's loops run, and, when backend alerts are on, a
    /// second health-check publisher. Does nothing beyond binding options when no provider is configured.
    /// </summary>
    public static IServiceCollection AddEarmarkedNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NotificationsOptions>().Bind(configuration.GetSection(NotificationsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        var notifications = configuration.GetSection(NotificationsOptions.Section).Get<NotificationsOptions>() ?? new NotificationsOptions();
        if (!notifications.Enabled)
        {
            return services;
        }

        switch (notifications.Provider)
        {
            case var p when string.Equals(p, NotificationProviders.Ntfy, StringComparison.OrdinalIgnoreCase):
                // The token or password goes in the Authorization header, which is redacted from HTTP logging.
                services.AddHttpClient(NtfyNotificationSender.HttpClientName, client =>
                    {
                        client.BaseAddress = new Uri(notifications.BaseUrl!.TrimEnd('/') + "/");
                        client.Timeout = TimeSpan.FromSeconds(20);
                        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                    })
                    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                    {
                        AllowAutoRedirect = false,
                        ConnectCallback = OutboundConnections.ConnectWithTimeout(TimeSpan.FromSeconds(10)),
                    })
                    .RedactLoggedHeaders(["Authorization"]);

                // One instance for both, so the health check sees whether the last message was refused.
                services.AddSingleton<NtfyNotificationSender>();
                services.AddSingleton<INotificationSender>(sp => sp.GetRequiredService<NtfyNotificationSender>());
                services.AddHealthChecks().AddCheck<NtfyHealthCheck>(BackendHealth.Notifications, tags: [BackendHealth.Tag], timeout: BackendHealth.Timeout);
                break;
        }

        services.AddSingleton<NotificationOutbox>();
        services.AddSingleton<NotificationDispatcher>();
        services.AddSingleton<DailySummary>();
        if (notifications.BackendAlerts)
        {
            services.AddSingleton<IHealthCheckPublisher, BackendAlertPublisher>();
        }

        return services;
    }

    private static void AddOpenLibrary(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OpenLibraryOptions>().Bind(configuration.GetSection(OpenLibraryOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        var openLibrary = configuration.GetSection(OpenLibraryOptions.Section).Get<OpenLibraryOptions>() ?? new OpenLibraryOptions();
        services.AddHttpClient(OpenLibraryCatalog.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(openLibrary.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(30);
                // The contact address is validated as a plain email address before it gets here (OpenLibraryOptions).
                client.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrEmpty(openLibrary.ContactEmail)
                    ? UserAgent
                    : UserAgent.Replace(")", $"; {openLibrary.ContactEmail})", StringComparison.Ordinal));
            })
            .ConfigurePrimaryHttpMessageHandler(CatalogHandler);
        services.AddKeyedSingleton<IBookCatalog, OpenLibraryCatalog>(BookLookupProviders.OpenLibrary);
    }

    private static void AddAppleBooks(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AppleBooksOptions>().Bind(configuration.GetSection(AppleBooksOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddHttpClient(AppleBooksCatalog.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("https://itunes.apple.com/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(CatalogHandler);
        services.AddKeyedSingleton<IBookCatalog, AppleBooksCatalog>(BookLookupProviders.AppleBooks);
    }

    /// <summary>
    /// Hardcover's client has a fixed https address, so the token can only ever go to Hardcover, and redacts the
    /// Authorization header from HTTP logging on top of that logging being held at Warning.
    /// </summary>
    private static void AddHardcover(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HardcoverOptions>().Bind(configuration.GetSection(HardcoverOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddHttpClient(HardcoverCatalog.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("https://api.hardcover.app/");
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(CatalogHandler)
            .RedactLoggedHeaders(["Authorization"]);
        services.AddKeyedSingleton<IBookCatalog, HardcoverCatalog>(BookLookupProviders.Hardcover);
    }

    /// <summary>
    /// Registers a catalogue's reachability check under <paramref name="healthName"/>. The check is a keyed singleton,
    /// so its five-minute memory of a healthy answer survives between runs.
    /// </summary>
    private static void AddCatalogHealthCheck(
        IServiceCollection services, string provider, string httpClientName, string healthName, IReadOnlyCollection<HttpStatusCode>? alsoUnavailable = null)
    {
        services.AddKeyedSingleton(provider, (sp, _) => new CatalogHealthCheck(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<TimeProvider>(), httpClientName, BookLookupProviders.Label(provider), alsoUnavailable));
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthName, sp => sp.GetRequiredKeyedService<CatalogHealthCheck>(provider), null, [BackendHealth.Tag], BackendHealth.Timeout));
    }

    /// <summary>Catalogue answers come from the internet: no redirects, and a connect that hangs fails fast.</summary>
    private static SocketsHttpHandler CatalogHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = OutboundConnections.ConnectWithTimeout(TimeSpan.FromSeconds(10)),
    };

    /// <summary>Handler for feed-supplied URLs: refuses private network addresses unless explicitly allowed.</summary>
    internal static SocketsHttpHandler CreateFeedHandler(HttpOptions http)
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        if (!http.AllowPrivateNetworkFeeds)
        {
            handler.UseProxy = false;
            handler.ConnectCallback = OutboundConnections.ConnectToPublicAddressAsync;
        }

        return handler;
    }

    private sealed class DatabaseHealthCheck(IDbContextFactory<AppDbContext> dbFactory) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Cannot open the SQLite database.");
        }
    }
}

/// <summary>The running build's version, as stamped by CI (e.g. <c>0.1.0+3f2a1bc</c>).</summary>
public static class AppVersion
{
    public static string Full { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>Version with the commit suffix shortened to 7 characters.</summary>
    public static string Display { get; } = Full.Split('+') is [var v, var sha] ? $"{v}+{sha[..Math.Min(7, sha.Length)]}" : Full;
}
