namespace PodcastBookRecs.Core.Providers.BookLookup;

/// <summary>Text helpers the catalogue adapters share.</summary>
internal static class CatalogText
{
    /// <summary>
    /// <paramref name="value"/> cut to at most <paramref name="max"/> UTF-16 units, one shorter if the cut would split a
    /// surrogate pair: half an emoji or CJK extension character would store as invalid UTF-16.
    /// </summary>
    public static string Cap(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        var end = max > 0 && char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return value[..end];
    }
}
