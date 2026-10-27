using Microsoft.Extensions.Logging;
using PodcastBookRecs.Web.Services;

namespace PodcastBookRecs.Tests;

/// <summary>How pages report failures: a fixed message on screen, the exception in the log.</summary>
public sealed class UiErrorTests
{
    [Fact]
    public void A_failed_load_is_logged_and_the_page_gets_a_fixed_message()
    {
        var logger = new RecordingLogger();

        var message = logger.LoadFailed(new InvalidOperationException("SQLite Error 1: no such table: /data/secret"), "this book");

        Assert.Equal("Couldn't load this book. The web app's log has the details.", message);
        Assert.Equal(LogLevel.Error, Assert.Single(logger.Levels));
    }

    [Fact]
    public void A_cancelled_load_is_not_logged()
    {
        var logger = new RecordingLogger();

        logger.LoadFailed(new TaskCanceledException(), "status");

        Assert.Empty(logger.Levels);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
