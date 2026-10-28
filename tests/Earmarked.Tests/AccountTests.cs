using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Earmarked.Core.Accounts;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Web.Services;

namespace Earmarked.Tests;

/// <summary>Runs the shipped Identity settings against a real, migrated SQLite file.</summary>
public sealed class AccountTests : IAsyncLifetime
{
    private const string Email = "reader@example.com";
    private const string Password = "a long test password";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"earmarked-test-{Guid.NewGuid():N}.db");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}").AddInterceptors(new SqlitePragmaInterceptor()));
        services.AddEarmarkedIdentity();
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

    [Fact]
    public async Task A_wrong_code_leaves_two_factor_off()
    {
        await CreateUserAsync();
        await using var scope = _services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdmin>();
        var (_, key) = await admin.StartAuthenticatorSetupAsync(Email);

        var (result, recoveryCodes) = await admin.ConfirmAuthenticatorAsync(Email, WrongCode(key!));

        Assert.False(result.Succeeded);
        Assert.Empty(recoveryCodes);
        Assert.False(await TwoFactorEnabledAsync());
    }

    [Fact]
    public async Task A_code_from_the_app_turns_on_two_factor_and_issues_recovery_codes_that_work_once()
    {
        await CreateUserAsync();
        var recoveryCodes = await EnrolAsync();

        Assert.True(await TwoFactorEnabledAsync());
        Assert.Equal(UserAdmin.RecoveryCodeCount, recoveryCodes.Count);

        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(Email);
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(user!, recoveryCodes[0])).Succeeded);
        Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(user!, recoveryCodes[0])).Succeeded);
    }

    [Fact]
    public async Task Codes_from_the_app_verify_at_sign_in()
    {
        await CreateUserAsync();
        await EnrolAsync();

        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(Email);
        var key = await users.GetAuthenticatorKeyAsync(user!);
        var provider = users.Options.Tokens.AuthenticatorTokenProvider;

        Assert.True(await users.VerifyTwoFactorTokenAsync(user!, provider, Totp.Code(key!, DateTimeOffset.UtcNow)));
        Assert.False(await users.VerifyTwoFactorTokenAsync(user!, provider, WrongCode(key!)));
    }

    [Fact]
    public async Task Enrolling_again_is_refused_while_two_factor_is_on_and_keeps_the_key()
    {
        await CreateUserAsync();
        await EnrolAsync();
        var (oldKey, _) = await KeyAndStampAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            var (result, key) = await scope.ServiceProvider.GetRequiredService<UserAdmin>().StartAuthenticatorSetupAsync(Email);
            Assert.False(result.Succeeded);
            Assert.Null(key);
        }

        Assert.Equal(oldKey, (await KeyAndStampAsync()).Key);
    }

    [Fact]
    public async Task After_disabling_a_new_device_can_be_enrolled()
    {
        await CreateUserAsync();
        await EnrolAsync();
        var (oldKey, _) = await KeyAndStampAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAdmin>().DisableTwoFactorAsync(Email);
        }

        await EnrolAsync();
        Assert.True(await TwoFactorEnabledAsync());
        Assert.NotEqual(oldKey, (await KeyAndStampAsync()).Key);
    }

    [Fact]
    public async Task Disabling_two_factor_discards_the_key_and_recovery_codes_and_signs_out_existing_sessions()
    {
        await CreateUserAsync();
        await EnrolAsync();
        var (oldKey, oldStamp) = await KeyAndStampAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserAdmin>().DisableTwoFactorAsync(Email)).Succeeded);
        }

        var (newKey, newStamp) = await KeyAndStampAsync();
        Assert.False(await TwoFactorEnabledAsync());
        Assert.NotEqual(oldKey, newKey);
        Assert.NotEqual(oldStamp, newStamp);

        await using var check = _services.CreateAsyncScope();
        var users = check.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.Equal(0, await users.CountRecoveryCodesAsync((await users.FindByEmailAsync(Email))!));
    }

    [Fact]
    public async Task Two_factor_commands_report_an_unknown_email()
    {
        await using var scope = _services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdmin>();

        Assert.False((await admin.StartAuthenticatorSetupAsync(Email)).Result.Succeeded);
        Assert.False((await admin.ConfirmAuthenticatorAsync(Email, "123456")).Result.Succeeded);
        Assert.False((await admin.DisableTwoFactorAsync(Email)).Succeeded);
    }

    /// <summary>Runs the same two steps as <c>users 2fa-enable</c>, answering with a code computed from the key.</summary>
    private async Task<IReadOnlyList<string>> EnrolAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var admin = scope.ServiceProvider.GetRequiredService<UserAdmin>();
        var (started, key) = await admin.StartAuthenticatorSetupAsync(Email);
        Assert.True(started.Succeeded);

        // Spaced the way apps display it, which the confirmation must accept.
        var code = Totp.Code(key!, DateTimeOffset.UtcNow).Insert(3, " ");
        var (confirmed, recoveryCodes) = await admin.ConfirmAuthenticatorAsync(Email, code);
        Assert.True(confirmed.Succeeded);
        return recoveryCodes;
    }

    private async Task<bool> TwoFactorEnabledAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.GetTwoFactorEnabledAsync((await users.FindByEmailAsync(Email))!);
    }

    private async Task<(string? Key, string? Stamp)> KeyAndStampAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByEmailAsync(Email))!;
        return (await users.GetAuthenticatorKeyAsync(user), await users.GetSecurityStampAsync(user));
    }

    /// <summary>A six-digit code that matches none of the time steps Identity accepts around now.</summary>
    private static string WrongCode(string key)
    {
        var now = DateTimeOffset.UtcNow;
        var valid = Enumerable.Range(-2, 5).Select(step => Totp.Code(key, now.AddSeconds(30 * step))).ToHashSet();
        return Enumerable.Range(0, 1_000_000).Select(n => n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).First(c => !valid.Contains(c));
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
