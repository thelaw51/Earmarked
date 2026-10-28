using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Earmarked.Core.Providers.BookLookup.OpenLibrary;

/// <summary>Open Library settings, read only when <c>BookLookup:Provider</c> is <c>OpenLibrary</c>.</summary>
public sealed partial class OpenLibraryOptions : IValidatableObject
{
    public const string Section = "OpenLibrary";

    /// <summary>
    /// Sent in the User-Agent, as Open Library asks of regular clients so they can get in touch about heavy use.
    /// Optional; the app paces itself the same either way. Never logged.
    /// </summary>
    public string? ContactEmail { get; set; }

    public string BaseUrl { get; set; } = "https://openlibrary.org";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Strict on purpose: the address goes into a request header, so nothing that could end or bend it gets through.
        if (!string.IsNullOrEmpty(ContactEmail) && !EmailPattern().IsMatch(ContactEmail))
        {
            yield return new ValidationResult("OpenLibrary:ContactEmail must be a plain email address, like you@example.com.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            yield return new ValidationResult("OpenLibrary:BaseUrl must be an absolute https URL.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,190}\.[A-Za-z]{2,24}$")]
    private static partial Regex EmailPattern();
}
