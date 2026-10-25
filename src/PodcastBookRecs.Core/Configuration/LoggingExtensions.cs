using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PodcastBookRecs.Core.Configuration;

/// <summary>Logging setup shared by both hosts.</summary>
public static class LoggingExtensions
{
    /// <summary>
    /// JSON console logs outside Development, and HttpClient request logging held at Warning: it logs full request
    /// URLs at Information, and private feed URLs carry a subscriber token in the path.
    /// </summary>
    public static ILoggingBuilder AddPbrLogging(this ILoggingBuilder logging, IHostEnvironment environment)
    {
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        if (!environment.IsDevelopment())
        {
            logging.ClearProviders();
            logging.AddJsonConsole(o =>
            {
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                o.UseUtcTimestamp = true;
            });
        }

        return logging;
    }
}
