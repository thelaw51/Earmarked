using System.ComponentModel.DataAnnotations;

namespace PodcastBookRecs.Core.Providers.BookLookup.Hardcover;

/// <summary>Hardcover settings, read only when <c>Hardcover</c> is the lookup provider or one of its fallbacks.</summary>
public sealed class HardcoverOptions : IValidatableObject
{
    public const string Section = "Hardcover";

    /// <summary>
    /// A personal API token from Hardcover's account settings. It only needs the <c>read:catalog</c> scope. Set it with
    /// the <c>PBR_Hardcover__Token</c> environment variable rather than in a file, and never log it.
    /// </summary>
    public string Token { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Messages describe the problem without the value: start-up errors end up in container logs.
        if (string.IsNullOrEmpty(Token))
        {
            yield return new ValidationResult("Hardcover:Token is needed to use Hardcover. Set it with the PBR_Hardcover__Token environment variable.");
        }
        else if (Token.Length > 1024 || Token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            yield return new ValidationResult("Hardcover:Token doesn't look like a token. Paste it as Hardcover shows it, without \"Bearer\" or spaces.");
        }
    }
}
