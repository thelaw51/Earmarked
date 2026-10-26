using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PodcastBookRecs.Core.Backends;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Pipeline;
using PodcastBookRecs.Worker.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPbrSources();
builder.Logging.AddPbrLogging(builder.Environment);

builder.Services.AddPbrCore(builder.Configuration);
builder.Services.AddPbrTranscription(builder.Configuration);
builder.Services.AddPbrExtraction(builder.Configuration);
builder.Services.Configure<HealthCheckPublisherOptions>(o =>
{
    o.Delay = TimeSpan.FromSeconds(10);
    o.Period = BackendHealth.Period;
    o.Predicate = check => check.Tags.Contains(BackendHealth.Tag);
});
builder.Services.AddSingleton<IHealthCheckPublisher, BackendStatusPublisher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FeedPoller>();
builder.Services.AddSingleton<TranscriptFetcher>();
builder.Services.AddSingleton<CommandProcessor>();
builder.Services.AddSingleton<FeedSeeder>();

// Registration order matters: the migrator's StartAsync finishes before the pipeline starts.
builder.Services.AddHostedService<DatabaseMigrator>();
builder.Services.AddHostedService<PipelineService>();

var app = builder.Build();
// Backend checks feed the Status page, not the container health: a Mac that's switched off shouldn't mark the Worker unhealthy.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => !check.Tags.Contains(BackendHealth.Tag) });
app.Run();
