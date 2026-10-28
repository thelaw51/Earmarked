using Microsoft.EntityFrameworkCore;
using Earmarked.Core.Data;
using Earmarked.Core.Feeds;

namespace Earmarked.Core.Books;

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

/// <summary>One mention behind a listed book, for exports. The podcast's title falls back to its feed's host.</summary>
public sealed record ListedMention(
    Guid BookId, Guid EpisodeId, string Episode, string Podcast, DateTime? PublishedAt, MentionKind Kind, TimeSpan? StartTime, string Quote);

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
        var facts = await InWindow(db, filter)
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

    /// <summary>
    /// The mentions that counted towards <paramref name="rows"/>: those in the filter's podcasts and dates, newest
    /// episode first.
    /// </summary>
    public static async Task<IReadOnlyList<ListedMention>> LoadMentionsAsync(
        AppDbContext db, BookFilter filter, IReadOnlyList<BookListRow> rows, CancellationToken cancellationToken)
    {
        // Every mention in the window is read and narrowed here, rather than sending the book IDs to SQL as a list.
        var listed = rows.Select(r => r.Book.Id).ToHashSet();
        var mentions = await InWindow(db, filter)
            .Select(m => new
            {
                BookId = m.BookId!.Value,
                m.EpisodeId,
                Episode = m.Episode.Title,
                m.Episode.PodcastFeed.Title,
                m.Episode.PodcastFeed.FeedUrl,
                m.Episode.PublishedAt,
                m.Kind,
                m.StartTime,
                m.Quote,
            })
            .ToListAsync(cancellationToken);
        // Sorted here: the SQLite provider can't order by a TimeSpan.
        return mentions
            .Where(m => listed.Contains(m.BookId))
            .OrderByDescending(m => m.PublishedAt)
            .ThenBy(m => m.StartTime)
            .Select(m => new ListedMention(
                m.BookId, m.EpisodeId, m.Episode, m.Title ?? FeedUrlRedactor.Redact(m.FeedUrl), m.PublishedAt, m.Kind, m.StartTime, m.Quote))
            .ToList();
    }

    /// <summary>Matched mentions from episodes in the filter's podcasts and dates.</summary>
    private static IQueryable<BookMention> InWindow(AppDbContext db, BookFilter filter)
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

        return mentions;
    }

    private static bool Matches(Book book, string text) =>
        book.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        || book.Authors.Contains(text, StringComparison.CurrentCultureIgnoreCase);
}
