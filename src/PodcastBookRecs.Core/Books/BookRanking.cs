using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Books;

/// <summary>One matched mention, reduced to what ranking needs.</summary>
public sealed record MentionFact(Guid BookId, Guid EpisodeId, Guid PodcastId, DateTime? PublishedAt, MentionKind Kind);

/// <summary>A book's place in the list: how widely it came up, and when it last did.</summary>
public sealed record RankedBook(Guid BookId, int Podcasts, int Episodes, int RecommendedIn, DateTime? LastMentioned);

/// <summary>
/// Orders books by how widely they've been talked about: the number of different podcasts first, then episodes, then
/// the most recent mention. Mention kinds don't move a book up or down, because the model labels them unreliably; how
/// many episodes recommended a book is reported alongside instead.
/// </summary>
public static class BookRanking
{
    public static IReadOnlyList<RankedBook> Rank(IEnumerable<MentionFact> mentions) =>
        mentions
            .GroupBy(m => m.BookId)
            .Select(g => new RankedBook(
                g.Key,
                g.Select(m => m.PodcastId).Distinct().Count(),
                g.Select(m => m.EpisodeId).Distinct().Count(),
                g.Where(m => m.Kind == MentionKind.Recommended).Select(m => m.EpisodeId).Distinct().Count(),
                g.Max(m => m.PublishedAt)))
            .OrderByDescending(r => r.Podcasts)
            .ThenByDescending(r => r.Episodes)
            .ThenByDescending(r => r.LastMentioned)
            .ThenBy(r => r.BookId)
            .ToList();
}
