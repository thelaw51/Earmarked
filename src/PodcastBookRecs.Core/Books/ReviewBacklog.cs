using Microsoft.EntityFrameworkCore;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Books;

/// <summary>Why a title is waiting for a person.</summary>
[Flags]
public enum ReviewReasons
{
    None = 0,
    NotFound = 1,
    LookupFailed = 2,
    LowConfidence = 4,
    NoAuthor = 8,
    FuzzySurname = 16,
    FewEditions = 32,
    OtherCatalogue = 64,
    ShortTitle = 128,
}

/// <summary>A passage where the title came up, with the episode it's from.</summary>
public sealed record ReviewQuote(Guid EpisodeId, string EpisodeTitle, string? PodcastTitle, string Quote, TimeSpan? StartTime, MentionKind Kind);

/// <summary>
/// One title waiting for review, identified by the mentions' stored <see cref="LookupKey"/>. <see cref="Title"/> and
/// <see cref="Author"/> are the newest mention's text as the model gave it.
/// </summary>
public sealed record ReviewItem(
    string LookupKey,
    string Title,
    string? Author,
    ReviewReasons Reasons,
    Book? Book,
    int Mentions,
    int Episodes,
    int Podcasts,
    DateTime? LastMentioned,
    IReadOnlyList<ReviewQuote> Quotes);

/// <summary>
/// Titles a person should look at: one item per lookup key with no <see cref="ReviewDecision"/>, where the catalogue
/// found nothing, the lookup failed, or the match is doubtful. Only mentions matched under the current
/// <see cref="CatalogMatcher.Version"/> count, so a key waiting to be matched again stays out until it has an answer.
/// </summary>
public static class ReviewBacklog
{
    /// <summary>
    /// A match is doubtful when even the most confident mention of the title is below this. The highest, so one sloppy
    /// mention doesn't flag a book that other episodes name with confidence. A starting value, to be tuned on real data.
    /// </summary>
    public const double LowConfidence = 0.6;

    public const int MaxQuotes = 3;

    public static Task<int> CountAsync(AppDbContext db, CancellationToken cancellationToken) => OpenKeys(db).CountAsync(cancellationToken);

    /// <summary>The queue, titles mentioned most first, then the most recently mentioned.</summary>
    public static async Task<IReadOnlyList<ReviewItem>> LoadAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var keys = await OpenKeys(db).ToListAsync(cancellationToken);
        if (keys.Count == 0)
        {
            return [];
        }

        var mentions = await db.BookMentions.AsNoTracking()
            .Where(m => m.LookupKey != null && keys.Contains(m.LookupKey) && m.MatcherVersion == CatalogMatcher.Version)
            .Select(m => new
            {
                Key = m.LookupKey!,
                m.Title,
                m.Author,
                m.MatchState,
                m.BookId,
                m.Confidence,
                m.Doubts,
                m.CreatedAt,
                Quote = new ReviewQuote(m.EpisodeId, m.Episode.Title, m.Episode.PodcastFeed.Title, m.Quote, m.StartTime, m.Kind),
                m.Episode.PodcastFeedId,
                m.Episode.PublishedAt,
            })
            .ToListAsync(cancellationToken);
        var bookIds = mentions.Where(m => m.BookId != null).Select(m => m.BookId!.Value).Distinct().ToList();
        var books = await db.Books.AsNoTracking().Where(b => bookIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);

        return mentions
            .GroupBy(m => m.Key)
            .Select(g =>
            {
                var latest = g.MaxBy(m => m.CreatedAt)!;
                var matched = g.Where(m => m.MatchState == MatchState.Matched && m.BookId != null).ToList();
                var reasons = ReviewReasons.None;
                if (g.Any(m => m.MatchState == MatchState.NoMatch))
                {
                    reasons |= ReviewReasons.NotFound;
                }

                if (g.Any(m => m.MatchState == MatchState.LookupFailed))
                {
                    reasons |= ReviewReasons.LookupFailed;
                }

                if (matched.Count > 0)
                {
                    if (matched.Max(m => m.Confidence) < LowConfidence)
                    {
                        reasons |= ReviewReasons.LowConfidence;
                    }

                    var doubts = matched.Aggregate(MatchDoubts.None, (all, m) => all | m.Doubts);
                    reasons |= (doubts.HasFlag(MatchDoubts.NoAuthor) ? ReviewReasons.NoAuthor : 0)
                        | (doubts.HasFlag(MatchDoubts.FuzzySurname) ? ReviewReasons.FuzzySurname : 0)
                        | (doubts.HasFlag(MatchDoubts.FewEditions) ? ReviewReasons.FewEditions : 0)
                        | (doubts.HasFlag(MatchDoubts.OtherCatalogue) ? ReviewReasons.OtherCatalogue : 0)
                        | (doubts.HasFlag(MatchDoubts.ShortTitle) ? ReviewReasons.ShortTitle : 0);
                }

                return new ReviewItem(
                    g.Key,
                    latest.Title,
                    latest.Author,
                    reasons,
                    matched.Count > 0 ? books.GetValueOrDefault(matched[0].BookId!.Value) : null,
                    g.Count(),
                    g.Select(m => m.Quote.EpisodeId).Distinct().Count(),
                    g.Select(m => m.PodcastFeedId).Distinct().Count(),
                    g.Max(m => m.PublishedAt),
                    g.OrderByDescending(m => m.Confidence).Select(m => m.Quote).Take(MaxQuotes).ToList());
            })
            .OrderByDescending(i => i.Mentions)
            .ThenByDescending(i => i.LastMentioned)
            .ThenBy(i => i.LookupKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Decisions people have made, newest first, with the book each points at.</summary>
    public static async Task<IReadOnlyList<ReviewDecision>> LoadDecidedAsync(AppDbContext db, CancellationToken cancellationToken) =>
        await db.ReviewDecisions.AsNoTracking().Include(d => d.Book).OrderByDescending(d => d.DecidedAt).ToListAsync(cancellationToken);

    /// <summary>
    /// The keys in the queue, as one grouped query so the nav badge can count them in SQL. A key qualifies when any of its
    /// mentions wasn't found or failed, any match has a doubt, or its most confident match is below <see cref="LowConfidence"/>.
    /// </summary>
    private static IQueryable<string> OpenKeys(AppDbContext db) =>
        db.BookMentions
            .Where(m => m.LookupKey != null && m.MatcherVersion == CatalogMatcher.Version)
            .Where(m => !db.ReviewDecisions.Any(d => d.LookupKey == m.LookupKey))
            .GroupBy(m => m.LookupKey!)
            .Where(g =>
                g.Count(m => m.MatchState == MatchState.NoMatch || m.MatchState == MatchState.LookupFailed) > 0
                || g.Count(m => m.MatchState == MatchState.Matched && m.Doubts != MatchDoubts.None) > 0
                || g.Max(m => m.MatchState == MatchState.Matched ? (double?)m.Confidence : null) < LowConfidence)
            .Select(g => g.Key);
}
