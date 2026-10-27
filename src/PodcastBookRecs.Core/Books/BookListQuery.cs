using Microsoft.EntityFrameworkCore;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Books;

/// <summary>
/// What the book list is narrowed to. Podcasts and dates pick which mentions count, so a book's numbers describe that
/// window. <paramref name="From"/> and <paramref name="To"/> are inclusive UTC bounds on the episode's publish time.
/// </summary>
public sealed record BookFilter(
    IReadOnlyCollection<Guid> PodcastIds,
    bool RecommendedOnly = false,
    DateTime? From = null,
    DateTime? To = null,
    string? Text = null)
{
    public static readonly BookFilter None = new([]);

    public bool IsEmpty => PodcastIds.Count == 0 && !RecommendedOnly && From is null && To is null && string.IsNullOrWhiteSpace(Text);
}

/// <summary>A book in the list. <see cref="Rank"/> is its place among the books mentioned in the filter's window.</summary>
public sealed record BookListRow(int Rank, Book Book, RankedBook Ranking);

/// <summary>
/// The ranked book list, filtered. The Books page and the exports share it, so an export holds exactly what the page
/// shows.
/// </summary>
public static class BookListQuery
{
    /// <summary>
    /// Ranks the books mentioned by episodes in the filter's podcasts and dates, then keeps those that pass the
    /// recommended and text filters. Those two don't renumber the list: a book keeps the rank it has in the window.
    /// </summary>
    public static async Task<IReadOnlyList<BookListRow>> LoadAsync(AppDbContext db, BookFilter filter, CancellationToken cancellationToken)
    {
        var mentions = db.BookMentions.AsNoTracking().Where(m => m.BookId != null);
        if (filter.PodcastIds.Count > 0)
        {
            var podcasts = filter.PodcastIds.ToList();
            mentions = mentions.Where(m => podcasts.Contains(m.Episode.PodcastFeedId));
        }

        if (filter.From is { } from)
        {
            mentions = mentions.Where(m => m.Episode.PublishedAt >= from);
        }

        if (filter.To is { } to)
        {
            mentions = mentions.Where(m => m.Episode.PublishedAt <= to);
        }

        var facts = await mentions
            .Select(m => new MentionFact(m.BookId!.Value, m.EpisodeId, m.Episode.PodcastFeedId, m.Episode.PublishedAt, m.Kind))
            .ToListAsync(cancellationToken);
        var books = await db.Books.AsNoTracking().ToDictionaryAsync(b => b.Id, cancellationToken);

        // Text matching runs here, not in SQL: SQLite's lower() and LIKE only fold ASCII, so "émile" would miss "Émile".
        var text = filter.Text?.Trim();
        return BookRanking.Rank(facts)
            .Where(r => books.ContainsKey(r.BookId))
            .Select((r, i) => new BookListRow(i + 1, books[r.BookId], r))
            .Where(r => !filter.RecommendedOnly || r.Ranking.RecommendedIn > 0)
            .Where(r => string.IsNullOrEmpty(text) || Matches(r.Book, text))
            .ToList();
    }

    private static bool Matches(Book book, string text) =>
        book.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        || book.Authors.Contains(text, StringComparison.CurrentCultureIgnoreCase);
}
