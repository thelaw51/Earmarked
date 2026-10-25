using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Accounts;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Web.Services;

namespace PodcastBookRecs.Tests;

/// <summary>Runs the shipped Identity settings against a real, migrated SQLite file.</summary>
public sealed class AccountTests : IAsyncLifetime
{
    private const string Email = "reader@example.com";
    private const string Password = "a long test password";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pbr-test-{Guid.NewGuid():N}.db");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}").AddInterceptors(new SqlitePragmaInterceptor()));
        services.AddPbrIdentity();
        services.AddAuthentication(IdentityConstants.ApplicationScheme);
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Migrations_match_the_model_the_app_runs_with()
    {
        await using var scope = _services.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Too_many_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        await CreateUserAsync();

        for (var i = 0; i < IdentityExtensions.MaxFailedAccessAttempts; i++)
        {
            Assert.False((await CheckPasswordAsync("wrong password, attempt " + i)).Succeeded);
        }

        var result = await CheckPasswordAsync(Password);
        Assert.False(result.Succeeded);
        Assert.True(result.IsLockedOut);
    }

    [Fact]
    public async Task One_fewer_wrong_password_than_the_limit_does_not_lock()
    {
        await CreateUserAsync();

        for (var i = 0; i < IdentityExtensions.MaxFailedAccessAttempts - 1; i++)
        {
            await CheckPasswordAsync("wrong password, attempt " + i);
        }

        Assert.True((await CheckPasswordAsync(Password)).Succeeded);
    }

    [Fact]
    public async Task Resetting_the_password_clears_the_lockout_and_old_password()
    {
        await CreateUserAsync();
        for (var i = 0; i < IdentityExtensions.MaxFailedAccessAttempts; i++)
        {
            await CheckPasswordAsync("wrong password, attempt " + i);
        }

        const string newPassword = "another long test password";
        await using (var scope = _services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserAdmin>().ResetPasswordAsync(Email, newPassword)).Succeeded);
        }

        Assert.False((await CheckPasswordAsync(Password)).IsLockedOut);
        Assert.True((await CheckPasswordAsync(newPassword)).Succeeded);
    }

    [Fact]
    public async Task A_rejected_reset_keeps_the_old_password()
    {
        await CreateUserAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            Assert.False((await scope.ServiceProvider.GetRequiredService<UserAdmin>().ResetPasswordAsync(Email, "short")).Succeeded);
        }

        Assert.True((await CheckPasswordAsync(Password)).Succeeded);
    }

    [Fact]
    public async Task A_password_reset_invalidates_existing_sign_ins()
    {
        await CreateUserAsync();

        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var user = await users.FindByEmailAsync(Email);
        var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>().CreateUserPrincipalAsync(user!);
        Assert.True(await IdentityRevalidatingAuthenticationStateProvider.IsCurrentAsync(users, principal, options));

        await using (var resetScope = _services.CreateAsyncScope())
        {
            await resetScope.ServiceProvider.GetRequiredService<UserAdmin>().ResetPasswordAsync(Email, "another long test password");
        }

        await using var checkScope = _services.CreateAsyncScope();
        Assert.False(await IdentityRevalidatingAuthenticationStateProvider.IsCurrentAsync(
            checkScope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(), principal, options));
    }

    [Theory]
    [InlineData("eleven char")]
    [InlineData("")]
    public async Task Short_passwords_are_rejected(string password)
    {
        await using var scope = _services.CreateAsyncScope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<UserAdmin>().CreateAsync(Email, password)).Succeeded);
    }

    [Fact]
    public async Task Passwords_need_length_not_character_classes()
    {
        await using var scope = _services.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserAdmin>().CreateAsync(Email, "twelve chars")).Succeeded);
    }

    private async Task CreateUserAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<UserAdmin>().CreateAsync(Email, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    /// <summary>The same check the sign-in page makes, minus the cookie, which needs an HTTP request.</summary>
    private async Task<SignInResult> CheckPasswordAsync(string password)
    {
        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>();
        var user = await users.FindByEmailAsync(Email);
        Assert.NotNull(user);
        return await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
    }
}
