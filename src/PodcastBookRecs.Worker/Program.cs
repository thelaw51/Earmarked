using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Pipeline;
using PodcastBookRecs.Worker.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPbrSources();
builder.Logging.AddPbrLogging(builder.Environment);

builder.Services.AddPbrCore(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FeedPoller>();
builder.Services.AddSingleton<TranscriptFetcher>();
builder.Services.AddSingleton<CommandProcessor>();
builder.Services.AddSingleton<FeedSeeder>();

// Registration order matters: the migrator's StartAsync finishes before the pipeline starts.
builder.Services.AddHostedService<DatabaseMigrator>();
builder.Services.AddHostedService<PipelineService>();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
