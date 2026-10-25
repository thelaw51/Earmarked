using Microsoft.AspNetCore.Identity;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Accounts;

/// <summary>
/// Creates users and resets passwords. The app has no sign-up or "forgot password" page, so this is the only way
/// in: the UI's <c>users</c> command calls it from a shell inside the container.
/// </summary>
public sealed class UserAdmin(UserManager<AppUser> users)
{
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
            return IdentityResult.Failed(new IdentityError { Code = "UserNotFound", Description = $"No user has the email {email}." });
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
}
