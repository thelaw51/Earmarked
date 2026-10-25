using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Web.Components;
using PodcastBookRecs.Web.Services;

// "users ..." manages accounts from a shell instead of starting the server. Its arguments aren't configuration.
var isUserCommand = args is [UserCommands.Name, ..];
var builder = WebApplication.CreateBuilder(isUserCommand ? [] : args);
builder.Configuration.AddPbrSources();
builder.Logging.AddPbrLogging(builder.Environment);

builder.Services.AddPbrCore(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SchemaStatus>();
builder.Services.AddMudServices();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Every endpoint needs a signed-in user unless it opts out with [AllowAnonymous] or AllowAnonymous().
builder.Services.AddPbrIdentity();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "pbr.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/logout";
    options.AccessDeniedPath = "/account/login";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = IdentityRevalidatingAuthenticationStateProvider.Interval);

// Keys live on the /data volume so a re-pulled container can still read circuits and antiforgery tokens.
var keysDirectory = new DirectoryInfo(
    builder.Configuration.GetSection(PathsOptions.Section).Get<PathsOptions>()?.KeysDirectory ?? new PathsOptions().KeysDirectory);
builder.Services.AddDataProtection().SetApplicationName("PodcastBookRecs").PersistKeysToFileSystem(keysDirectory);

var app = builder.Build();

if (isUserCommand)
{
    return await UserCommands.RunAsync(app.Services, args);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// The sign-in page needs its CSS and scripts before anyone has signed in, and Docker's health check never signs in.
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
// Blazor's default allows same-origin framing. Nothing here is meant to be embedded, and a framed sign-in page
// invites clickjacking.
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o => o.ContentSecurityFrameAncestorsPolicy = "'none'");

app.Run();
return 0;
