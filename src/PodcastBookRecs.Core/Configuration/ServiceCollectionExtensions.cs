using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Configuration;

/// <summary>Configuration and service registration shared by the Worker and the UI.</summary>
public static class ServiceCollectionExtensions
{
    public const string EnvironmentPrefix = "PBR_";
    public const string FeedHttpClient = "feeds";

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

    /// <summary>Registers options, the SQLite context factory, the feed HTTP client and the database health check.</summary>
    public static IServiceCollection AddPbrCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PathsOptions>().Bind(configuration.GetSection(PathsOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<PollingOptions>().Bind(configuration.GetSection(PollingOptions.Section)).ValidateDataAnnotations().ValidateOnStart();

        services.AddDbContextFactory<AppDbContext>((sp, options) =>
        {
            var paths = sp.GetRequiredService<IOptions<PathsOptions>>().Value;
            options.UseSqlite($"Data Source={paths.DatabaseFile}");
            options.AddInterceptors(new SqlitePragmaInterceptor());
        });

        services.AddHttpClient(FeedHttpClient, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(2);
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"PodcastBookRecs/{AppVersion.Display} (+https://github.com/thelaw51/PodcastBookRecs)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
        return services;
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
