using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Earmarked.Core.Providers.BookLookup.AppleBooks;

/// <summary>Apple Books settings, read only when <c>AppleBooks</c> is the lookup provider or one of its fallbacks.</summary>
public sealed partial class AppleBooksOptions : IValidatableObject
{
    public const string Section = "AppleBooks";

    /// <summary>The store to search, as a two-letter country code such as <c>us</c> or <c>gb</c>. Stores list different books.</summary>
    public string Country { get; set; } = "us";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // It goes into the query string and every book link, so only two lower-case letters get through.
        if (!CountryPattern().IsMatch(Country))
        {
            yield return new ValidationResult("AppleBooks:Country must be a two-letter country code in lower case, like us or gb.");
        }
    }

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex CountryPattern();
}
