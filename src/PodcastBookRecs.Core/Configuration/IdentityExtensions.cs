using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PodcastBookRecs.Core.Accounts;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Configuration;

/// <summary>Identity settings shared by the UI and the tests, so the lockout the tests check is the one that ships.</summary>
public static class IdentityExtensions
{
    public const int MaxFailedAccessAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public const int MinimumPasswordLength = 12;

    /// <summary>
    /// Registers user management with lockout on and no email confirmation, since the app never sends email.
    /// Passwords need length only: composition rules push people towards predictable passwords.
    /// </summary>
    public static IdentityBuilder AddPbrIdentity(this IServiceCollection services) =>
        services.AddScoped<UserAdmin>().AddIdentityCore<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
                options.SignIn.RequireConfirmedEmail = false;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
                options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;

                options.Password.RequiredLength = MinimumPasswordLength;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager();
}
