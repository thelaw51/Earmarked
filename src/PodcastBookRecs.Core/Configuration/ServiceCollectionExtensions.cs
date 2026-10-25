using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Core.Net;

namespace PodcastBookRecs.Core.Configuration;

/// <summary>Configuration and service registration shared by the Worker and the UI.</summary>
public static class ServiceCollectionExtensions
{
    public const string EnvironmentPrefix = "PBR_";
    public const string FeedHttpClient = "feeds";
    public const string EnclosureHttpClient = "enclosures";

    private static readonly string UserAgent = $"PodcastBookRecs/{AppVersion.Display} (+https://github.com/thelaw51/PodcastBookRecs)";

    /// <summary>
    /// Adds <c>config.json</c> from the config directory (<c>/config</c> unless <c>PBR_CONFIG_DIR</c> says otherwise),
    /// then <c>PBR_</c> environment variables on top, e.g. <c>PBR_Polling__IntervalMinutes=30</c>.
    /// </summary>
    public static IConfigurationBuilder AddPbrSources(this IConfigurationBuilder configuration)
    {
        var configDir = Environment.GetEnvironmentVariable(EnvironmentPrefix + "CONFIG_DIR") ?? "/config";
        return configuration
            .AddJsonFile(Path.Combine(Path.GetFullPath(configDir), "config.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(EnvironmentPrefix);
    }

    /// <summary>Registers options, the SQLite context factory, the feed and audio HTTP clients and the database health check.</summary>
    public static IServiceCollection AddPbrCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PathsOptions>().Bind(configuration.GetSection(PathsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PollingOptions>().Bind(configuration.GetSection(PollingOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<HttpOptions>().Bind(configuration.GetSection(HttpOptions.Section));

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
