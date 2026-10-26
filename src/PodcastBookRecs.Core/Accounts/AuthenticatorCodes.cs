namespace PodcastBookRecs.Core.Accounts;

/// <summary>Formatting for authenticator-app enrolment and the codes people type at sign-in.</summary>
public static class AuthenticatorCodes
{
    public const string Issuer = "PodcastBookRecs";

    /// <summary>Strips the spaces and hyphens that apps show to make a six-digit code easier to read.</summary>
    public static string Normalise(string code) => code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    /// <summary>
    /// True for a six-digit authenticator code, after <see cref="Normalise"/>. Anything else is tried as a recovery
    /// code, which Identity generates as two groups of five letters and digits.
    /// </summary>
    public static bool IsAuthenticatorCode(string code) => Normalise(code) is { Length: 6 } digits && digits.All(char.IsAsciiDigit);

    /// <summary>
    /// The key in lowercase groups of four, for typing into an app by hand. Base32 ignores case and apps accept
    /// either, and lowercase is easier to read; Identity's own account pages show it the same way.
    /// </summary>
    public static string FormatKey(string key) => string.Join(' ', key.Chunk(4).Select(group => new string(group))).ToLowerInvariant();

    /// <summary>
    /// The <c>otpauth://</c> link that authenticator apps read from a QR code. Pasting it into a QR generator or a
    /// password manager such as 1Password enrols in one step.
    /// </summary>
    public static Uri EnrolmentUri(string email, string key) =>
        new($"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(email)}?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits=6&period=30");
}
