using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Web.Components;
using PodcastBookRecs.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPbrSources();
builder.Logging.AddPbrLogging(builder.Environment);

builder.Services.AddPbrCore(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SchemaStatus>();
builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Keys live on the /data volume so a re-pulled container can still read circuits and antiforgery tokens.
var keysDirectory = new DirectoryInfo(
    builder.Configuration.GetSection(PathsOptions.Section).Get<PathsOptions>()?.KeysDirectory ?? new PathsOptions().KeysDirectory);
builder.Services.AddDataProtection().SetApplicationName("PodcastBookRecs").PersistKeysToFileSystem(keysDirectory);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
