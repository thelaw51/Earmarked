using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Earmarked.Core.Accounts;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;

namespace Earmarked.Tests;

/// <summary>
/// Runs the two sign-in steps through <see cref="SignInManager{TUser}"/> with real Identity cookies, carried from one
/// request to the next the way a browser would. These pin lockout behaviour that lives inside Identity and could
/// change with a framework upgrade.
/// </summary>
public sealed class SignInFlowTests : IAsyncLifetime
{
    private const string Email = "reader@example.com";
    private const string Password = "a long test password";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"earmarked-test-{Guid.NewGuid():N}.db");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CountingHasher _hasher = new();
    private ServiceProvider _services = null!;
    private string _key = null!;
    private IReadOnlyList<string> _recoveryCodes = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}").AddInterceptors(new SqlitePragmaInterceptor()));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        // Before AddEarmarkedIdentity, which only adds a hasher if none is registered.
        services.AddSingleton<IPasswordHasher<AppUser>>(_hasher);
        services.AddEarmarkedIdentity();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddHttpContextAccessor();
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync(Ct);
        var admin = scope.ServiceProvider.GetRequiredService<UserAdmin>();
        Assert.True((await admin.CreateAsync(Email, Password)).Succeeded);
        _key = (await admin.StartAuthenticatorSetupAsync(Email)).Key!;
        var (confirmed, codes) = await admin.ConfirmAuthenticatorAsync(Email, Totp.Code(_key, DateTimeOffset.UtcNow));
        Assert.True(confirmed.Succeeded);
        _recoveryCodes = codes;
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
    public async Task A_right_password_asks_for_a_code_and_a_right_code_signs_in()
    {
        var (step1, cookies) = await RequestAsync("", s => s.PasswordSignInAsync(Email, Password, false, lockoutOnFailure: true));
        Assert.True(step1.RequiresTwoFactor);

        var (step2, _) = await RequestAsync(cookies, s => s.TwoFactorAuthenticatorSignInAsync(Totp.Code(_key, DateTimeOffset.UtcNow), false, false));
        Assert.True(step2.Succeeded);
    }

    [Fact]
    public async Task Wrong_codes_lock_the_account_even_when_the_password_is_entered_again_in_between()
    {
        var wrong = WrongCode();
        for (var i = 0; i < IdentityExtensions.MaxFailedAccessAttempts - 1; i++)
        {
            // A fresh password step each time: it must not reset the count of wrong codes.
            var (step1, cookies) = await RequestAsync("", s => s.PasswordSignInAsync(Email, Password, false, lockoutOnFailure: true));
            Assert.True(step1.RequiresTwoFactor);
            var (step2, _) = await RequestAsync(cookies, s => s.TwoFactorAuthenticatorSignInAsync(wrong, false, false));
            Assert.False(step2.Succeeded);
            Assert.Equal(i + 1, await AccessFailedCountAsync());
        }

        var (_, lastCookies) = await RequestAsync("", s => s.PasswordSignInAsync(Email, Password, false, lockoutOnFailure: true));
        var (last2, _) = await RequestAsync(lastCookies, s => s.TwoFactorAuthenticatorSignInAsync(wrong, false, false));
        Assert.True(last2.IsLockedOut);

        var (afterwards, _) = await RequestAsync(lastCookies, s => s.TwoFactorAuthenticatorSignInAsync(Totp.Code(_key, DateTimeOffset.UtcNow), false, false));
        Assert.False(afterwards.Succeeded);
    }

    [Fact]
    public async Task A_recovery_code_typed_in_lowercase_without_its_hyphen_signs_in_once()
    {
        var typed = _recoveryCodes[0].Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

        var (step1, cookies) = await RequestAsync("", s => s.PasswordSignInAsync(Email, Password, false, lockoutOnFailure: true));
        Assert.True(step1.RequiresTwoFactor);
        var (first, _) = await RequestAsync(cookies, s => s.TwoFactorRecoveryCodeSignInAsync(AuthenticatorCodes.NormaliseRecoveryCode(typed)));
        Assert.True(first.Succeeded);

        var (_, againCookies) = await RequestAsync("", s => s.PasswordSignInAsync(Email, Password, false, lockoutOnFailure: true));
        var (again, _) = await RequestAsync(againCookies, s => s.TwoFactorRecoveryCodeSignInAsync(AuthenticatorCodes.NormaliseRecoveryCode(typed)));
        Assert.False(again.Succeeded);
    }

    [Fact]
    public async Task An_unknown_email_costs_a_password_hash_like_a_real_account()
    {
        var before = _hasher.Verifications;
        var (unknown, _) = await RequestAsync("", s => PasswordSignIn.SignInAsync(s, "nobody@example.com", Password, false));
        Assert.False(unknown.Succeeded);
        Assert.Equal(before + 1, _hasher.Verifications);

        var (wrong, _) = await RequestAsync("", s => PasswordSignIn.SignInAsync(s, Email, "wrong password", false));
        Assert.False(wrong.Succeeded);
        Assert.Equal(before + 2, _hasher.Verifications);
    }

    [Fact]
    public async Task A_locked_account_costs_a_password_hash_and_still_refuses_the_right_password()
    {
        for (var i = 0; i < IdentityExtensions.MaxFailedAccessAttempts; i++)
        {
            await RequestAsync("", s => PasswordSignIn.SignInAsync(s, Email, "wrong password", false));
        }

        var before = _hasher.Verifications;
        var (locked, _) = await RequestAsync("", s => PasswordSignIn.SignInAsync(s, Email, Password, false));
        Assert.True(locked.IsLockedOut);
        Assert.Equal(before + 1, _hasher.Verifications);
    }

    [Fact]
    public async Task The_timed_sign_in_still_asks_a_real_account_for_its_code()
    {
        var (result, _) = await RequestAsync("", s => PasswordSignIn.SignInAsync(s, Email, Password, false));
        Assert.True(result.RequiresTwoFactor);
    }

    /// <summary>
    /// Runs <paramref name="action"/> as one request that sends <paramref name="cookieHeader"/>, and returns the cookies
    /// the response set, ready to send with the next request.
    /// </summary>
    private async Task<(SignInResult Result, string Cookies)> RequestAsync(string cookieHeader, Func<SignInManager<AppUser>, Task<SignInResult>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Headers.Cookie = cookieHeader;
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;

        var result = await action(scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>());
        var cookies = context.Response.Headers.SetCookie.Select(c => c!.Split(';')[0]).Where(c => !c.EndsWith('='));
        return (result, string.Join("; ", cookies));
    }

    private async Task<int> AccessFailedCountAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.GetAccessFailedCountAsync((await users.FindByEmailAsync(Email))!);
    }

    /// <summary>A six-digit code that matches none of the time steps Identity accepts around now.</summary>
    private string WrongCode()
    {
        var now = DateTimeOffset.UtcNow;
        var valid = Enumerable.Range(-2, 5).Select(step => Totp.Code(_key, now.AddSeconds(30 * step))).ToHashSet();
        return Enumerable.Range(0, 1_000_000).Select(n => n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)).First(c => !valid.Contains(c));
    }

    /// <summary>The real hasher, counting how often a password is checked.</summary>
    private sealed class CountingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();
        private int _verifications;

        public int Verifications => Volatile.Read(ref _verifications);

        public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
