namespace Earmarked.Core.BookLookup;

/// <summary>
/// The configured catalogues in the order they are asked: <c>BookLookup:Provider</c> first, then each of
/// <c>BookLookup:Fallbacks</c>. The matcher asks a fallback only when the catalogues before it found nothing; the
/// review page's search asks them all. Each adapter is a singleton, so its pacing covers both.
/// </summary>
public sealed class BookCatalogChain(IReadOnlyList<IBookCatalog> catalogs)
{
    public IReadOnlyList<IBookCatalog> All { get; } = catalogs.Count > 0 ? catalogs : throw new ArgumentException("A chain needs a catalogue.", nameof(catalogs));

    public IBookCatalog Primary => All[0];
}
