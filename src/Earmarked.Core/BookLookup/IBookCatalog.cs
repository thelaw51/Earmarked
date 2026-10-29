using Earmarked.Core.Configuration;

namespace Earmarked.Core.BookLookup;

/// <summary>
/// A searchable catalogue of published books, used to turn a title the model heard into a real book. The pipeline
/// depends only on this interface; each catalogue gets its own adapter under <c>Providers/BookLookup/</c>.
/// </summary>
public interface IBookCatalog
{
    /// <summary>Stored on every book found through this catalogue, e.g. <c>OpenLibrary</c>.</summary>
    string Provider { get; }

    /// <summary>
    /// Returns the catalogue's best candidates for a title, most relevant first, filtered by author when one is given.
    /// Candidates are unchecked: <see cref="CatalogMatcher"/> decides whether any of them is the book.
    /// </summary>
    /// <exception cref="Backends.BackendUnavailableException">The catalogue can't be reached or asked us to slow down.</exception>
    /// <exception cref="CatalogLookupFailedException">The catalogue answered, but not with a usable result.</exception>
    Task<IReadOnlyList<CatalogBook>> SearchAsync(string title, string? author, CancellationToken cancellationToken);

    /// <summary>
    /// Returns what the catalogue says about one of its own books (<see cref="Data.Book.CatalogId"/>), or null when it no
    /// longer has that book. Text comes back as plain text, already capped to what the database stores.
    /// </summary>
    /// <exception cref="Backends.BackendUnavailableException">The catalogue can't be reached or asked us to slow down.</exception>
    /// <exception cref="CatalogLookupFailedException">The catalogue answered, but not with a usable result.</exception>
    Task<BookDetails?> GetDetailsAsync(string catalogId, CancellationToken cancellationToken);
}

/// <summary>
/// What a catalogue knows about a book beyond what its search returns, in our terms. Every field is optional because
/// no catalogue has all of them: Open Library has no ratings or page count for a work, Apple Books no page count.
/// <paramref name="Rating"/> is an average out of 5 from <paramref name="RatingCount"/> of the catalogue's own users.
/// </summary>
public sealed record BookDetails(
    string? Description,
    IReadOnlyList<string> Subjects,
    int? PageCount,
    double? Rating,
    int? RatingCount)
{
    public const int MaxSubjects = 12;
    public const int MaxSubjectLength = 60;
}

/// <summary>
/// One catalogue entry, in our terms. The adapter builds <paramref name="InfoUrl"/> and <paramref name="CoverUrl"/>
/// from identifiers it has checked, so they are https links on the catalogue's own hosts and the UI needs no
/// knowledge of the catalogue's URL scheme. Only a <see cref="BookLookupProviders.Manual"/> entry, typed in on the
/// review page, has no <paramref name="InfoUrl"/>. <paramref name="EditionCount"/> is null when the catalogue doesn't
/// count editions. <paramref name="Provider"/> is null in entries stored before
/// catalogues recorded it, when Open Library was the only one; read it through <see cref="ProviderOrDefault"/>.
/// </summary>
public sealed record CatalogBook(
    string CatalogId,
    string Title,
    string? Subtitle,
    IReadOnlyList<string> Authors,
    int? FirstPublishYear,
    int? EditionCount,
    string? InfoUrl,
    string? CoverUrl,
    string? Provider = null)
{
    /// <summary>The catalogue this entry came from, with entries stored before it was recorded counted as Open Library.</summary>
    public string ProviderOrDefault => Provider ?? BookLookupProviders.OpenLibrary;
}

/// <summary>The catalogue answered with something that can't be used for this title. Retrying the same title won't help.</summary>
public sealed class CatalogLookupFailedException(string message, Exception? innerException = null) : Exception(message, innerException);
