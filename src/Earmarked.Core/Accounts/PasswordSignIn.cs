using Microsoft.AspNetCore.Identity;
using Earmarked.Core.Data;

namespace Earmarked.Core.Accounts;

/// <summary>
/// The sign-in page's password check, timed the same whether or not the email has an account. Identity returns at
/// once for an unknown email and for a locked account, but hashes the password for a real, unlocked one, so the
/// response time alone would tell an attacker which emails have accounts.
/// </summary>
public static class PasswordSignIn
{
    // Computed on first use with the app's own hasher, so it costs what a real check costs. A race just computes it twice.
    private static string? _placeholderHash;

    /// <summary>
    /// Signs in with lockout on. For an unknown email or a locked account it checks the password against a placeholder
    /// hash first, so every answer costs one hash.
    /// </summary>
    public static async Task<SignInResult> SignInAsync(SignInManager<AppUser> signIn, string email, string password, bool isPersistent)
    {
        var users = signIn.UserManager;
        if (await users.FindByNameAsync(email) is not { } user)
        {
            HashPlaceholder(users, password);
            return SignInResult.Failed;
        }

        if (await users.IsLockedOutAsync(user))
        {
            HashPlaceholder(users, password);
            return SignInResult.LockedOut;
        }

        return await signIn.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure: true);
    }

    private static void HashPlaceholder(UserManager<AppUser> users, string password)
    {
        var placeholder = new AppUser();
        _placeholderHash ??= users.PasswordHasher.HashPassword(placeholder, Guid.NewGuid().ToString());
        users.PasswordHasher.VerifyHashedPassword(placeholder, _placeholderHash, password);
    }
}
