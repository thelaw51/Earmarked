using Microsoft.AspNetCore.Identity;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Accounts;

/// <summary>
/// Creates users and resets passwords. The app has no sign-up or "forgot password" page, so this is the only way
/// in: the UI's <c>users</c> command calls it from a shell inside the container.
/// </summary>
public sealed class UserAdmin(UserManager<AppUser> users)
{
    public const int RecoveryCodeCount = 10;

    /// <summary>Creates a user whose user name is their email address, with lockout enabled.</summary>
    public Task<IdentityResult> CreateAsync(string email, string password) =>
        users.CreateAsync(new AppUser { UserName = email, Email = email }, password);

    /// <summary>
    /// Sets a new password, clears any lockout and changes the security stamp, which signs out existing sessions the
    /// next time their cookie is validated. Saves once, so a rejected password leaves the old one in place.
    /// </summary>
    public async Task<IdentityResult> ResetPasswordAsync(string email, string password)
    {
        if (await users.FindByEmailAsync(email) is not { } user)
        {
            return NotFound(email);
        }

        foreach (var validator in users.PasswordValidators)
        {
            var validation = await validator.ValidateAsync(users, user, password);
            if (!validation.Succeeded)
            {
                return validation;
            }
        }

        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        return await users.UpdateSecurityStampAsync(user);
    }

    /// <summary>
    /// Gives the user a new authenticator key and returns it. Two-factor sign-in stays off until
    /// <see cref="ConfirmAuthenticatorAsync"/> accepts a code, so a mistyped key can't lock anyone out. Refused while
    /// two-factor sign-in is on: replacing the key then would leave it on with a key no app has until a code is
    /// confirmed. The new security stamp signs out existing sessions.
    /// </summary>
    public async Task<(IdentityResult Result, string? Key)> StartAuthenticatorSetupAsync(string email)
    {
        if (await users.FindByEmailAsync(email) is not { } user)
        {
            return (NotFound(email), null);
        }

        if (await users.GetTwoFactorEnabledAsync(user))
        {
            return (IdentityResult.Failed(new IdentityError
            {
                Code = "AlreadyEnabled",
                Description = $"Two-factor sign-in is already on for {email}. To move to a new device, turn it off first with 2fa-disable.",
            }), null);
        }

        var result = await users.ResetAuthenticatorKeyAsync(user);
        return result.Succeeded ? (result, await users.GetAuthenticatorKeyAsync(user)) : (result, null);
    }

    /// <summary>
    /// Turns on two-factor sign-in if <paramref name="code"/> matches the user's authenticator key, and returns fresh
    /// recovery codes, which replace any earlier ones.
    /// </summary>
    public async Task<(IdentityResult Result, IReadOnlyList<string> RecoveryCodes)> ConfirmAuthenticatorAsync(string email, string code)
    {
        if (await users.FindByEmailAsync(email) is not { } user)
        {
            return (NotFound(email), []);
        }

        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, AuthenticatorCodes.Normalise(code)))
        {
            return (IdentityResult.Failed(new IdentityError { Code = "InvalidCode", Description = "That code doesn't match. Check the key and the time on your device." }), []);
        }

        var result = await users.SetTwoFactorEnabledAsync(user, true);
        if (!result.Succeeded)
        {
            return (result, []);
        }

        return (result, [.. await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount) ?? []]);
    }

    /// <summary>
    /// Turns off two-factor sign-in and discards the authenticator key, for a lost or replaced device. Leftover recovery
    /// codes can't be used without two-factor sign-in, and enrolling again replaces them. Existing sessions are signed out.
    /// </summary>
    public async Task<IdentityResult> DisableTwoFactorAsync(string email)
    {
        if (await users.FindByEmailAsync(email) is not { } user)
        {
            return NotFound(email);
        }

        var result = await users.SetTwoFactorEnabledAsync(user, false);
        return result.Succeeded ? await users.ResetAuthenticatorKeyAsync(user) : result;
    }

    private static IdentityResult NotFound(string email) =>
        IdentityResult.Failed(new IdentityError { Code = "UserNotFound", Description = $"No user has the email {email}." });
}
