using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Web.Services;

/// <summary>
/// Re-checks a circuit's user against the database. The cookie is only checked on HTTP requests, and a circuit makes
/// none once its WebSocket is open, so without this an open tab outlives a password reset. When the security stamp
/// no longer matches, the circuit becomes signed out and <c>Routes</c> sends the browser to the sign-in page.
/// </summary>
internal sealed class IdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    /// <summary>Also used for the cookie's own stamp check, so circuits and plain requests end on the same schedule.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override TimeSpan RevalidationInterval => Interval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await IsCurrentAsync(users, authenticationState.User, options.Value);
    }

    /// <summary>True while the user still exists and the principal's security stamp matches the stored one.</summary>
    internal static async Task<bool> IsCurrentAsync(UserManager<AppUser> users, ClaimsPrincipal principal, IdentityOptions options)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return false;
        }

        var principalStamp = principal.FindFirstValue(options.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp is not null && principalStamp == await users.GetSecurityStampAsync(user);
    }
}
