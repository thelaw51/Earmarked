using Microsoft.AspNetCore.Identity;

namespace Earmarked.Core.Data;

/// <summary>
/// Someone who can sign in to the UI. The user name is the email address. Identity's <c>LockoutEnd</c> is a
/// <see cref="DateTimeOffset"/>, the one exception to the UTC <see cref="DateTime"/> rule; Identity compares it in
/// memory, never in SQL, so the SQLite provider's limits don't apply.
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public AppUser() => Id = Guid.CreateVersion7();
}
