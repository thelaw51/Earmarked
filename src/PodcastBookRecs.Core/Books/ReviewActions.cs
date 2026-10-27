using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Core.Books;

/// <summary>
/// A review action refused for a reason the person can act on. The review page shows the message as it is, so it must
/// be written for them and never carry another exception's text.
/// </summary>
public sealed class ReviewActionException(string message) : Exception(message);

/// <summary>
/// What the review page writes. Each decision, change or undo is saved in one <c>SaveChanges</c> together with the
/// <see cref="WorkerCommandType.ReviewDecided"/> command that makes the Worker re-match the key, so neither is ever
/// stored without the other. Everything the page sends is treated as untrusted: the key must belong to stored mentions,
/// whose title and author the decision records, notes and search text are trimmed and capped, a confirmed book comes
/// from the mentions, and a pick comes from a stored search result or, for "keep as is", is built here.
/// </summary>
public static class ReviewActions
{
    /// <summary>More open searches than this and a new one is refused, so a burst can't hold up book matching.</summary>
    public const int MaxOpenSearches = 5;

    /// <summary>Confirms the catalogue's match, using the book the title's mentions are matched to now.</summary>
    public static async Task<WorkerCommand> ConfirmAsync(AppDbContext db, string key, string? note, DateTime now, CancellationToken cancellationToken)
    {
        var (title, author) = await MentionedAsync(db, key, cancellationToken);
        var bookId = await db.BookMentions
            .Where(m => m.LookupKey == key && m.MatchState == MatchState.Matched && m.BookId != null)
            .Select(m => m.BookId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ReviewActionException("This title isn't matched to a book, so there's nothing to confirm.");
        return await SaveAsync(db, key, title, author, ReviewVerdict.Confirmed, bookId, NoPick, Clean(note, ReviewDecision.MaxNoteLength), now, cancellationToken);
    }

    /// <summary>Rules a title out: not a book at all, or not the book the catalogue found.</summary>
    public static async Task<WorkerCommand> RejectAsync(
        AppDbContext db, string key, ReviewVerdict verdict, string? note, DateTime now, CancellationToken cancellationToken)
    {
        if (verdict is not (ReviewVerdict.NotABook or ReviewVerdict.WrongBook))
        {
            throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Only NotABook and WrongBook rule a title out.");
        }

        var (title, author) = await MentionedAsync(db, key, cancellationToken);
        return await SaveAsync(db, key, title, author, verdict, null, NoPick, Clean(note, ReviewDecision.MaxNoteLength), now, cancellationToken);
    }

    /// <summary>
    /// Points a title at a book the person picked from a search. The pick is copied from the stored search result with
    /// that catalogue ID, never from the page, and checked the way the matcher checks it.
    /// </summary>
    public static async Task<WorkerCommand> CorrectAsync(
        AppDbContext db, string key, Guid searchId, string catalogId, string? note, DateTime now, CancellationToken cancellationToken)
    {
        var results = await db.CatalogSearches.AsNoTracking().Where(s => s.Id == searchId).Select(s => s.Results).FirstOrDefaultAsync(cancellationToken);
        var pick = results?.FirstOrDefault(b => b.CatalogId == catalogId);
        if (!CatalogMatcher.IsUsable(pick) || pick!.ProviderOrDefault == BookLookupProviders.Manual)
        {
            throw new ReviewActionException("That search result is no longer available. Search again.");
        }

        var (title, author) = await MentionedAsync(db, key, cancellationToken);
        return await SaveAsync(db, key, title, author, ReviewVerdict.Corrected, null, _ => pick, Clean(note, ReviewDecision.MaxNoteLength), now, cancellationToken);
    }

    /// <summary>
    /// Keeps a title as a book of its own, for a real book no catalogue has. The person may fix the title and author
    /// first; that text goes only into the pick, while the decision still records the mention's own text, which is
    /// what its key was built from. The pick's ID is the decision's, so keeping the same key again with new text
    /// updates that one book.
    /// </summary>
    public static async Task<WorkerCommand> KeepAsIsAsync(
        AppDbContext db, string key, string? keptTitle, string? keptAuthor, string? note, DateTime now, CancellationToken cancellationToken)
    {
        var cleanTitle = Clean(Typed(keptTitle), BookMention.MaxTitleLength) ?? throw new ReviewActionException("Enter the book's title.");
        var cleanAuthor = Clean(Typed(keptAuthor), BookMention.MaxAuthorLength);
        var (title, author) = await MentionedAsync(db, key, cancellationToken);
        return await SaveAsync(db, key, title, author, ReviewVerdict.KeptAsIs, null,
            id => new CatalogBook(id.ToString("N"), cleanTitle, null, cleanAuthor is null ? [] : [cleanAuthor], null, 0, null, null, BookLookupProviders.Manual),
            Clean(note, ReviewDecision.MaxNoteLength), now, cancellationToken);
    }

    /// <summary>Deletes a decision, so the title is matched normally again.</summary>
    public static async Task<WorkerCommand> UndoAsync(AppDbContext db, Guid decisionId, DateTime now, CancellationToken cancellationToken)
    {
        var decision = await db.ReviewDecisions.SingleOrDefaultAsync(d => d.Id == decisionId, cancellationToken)
            ?? throw new ReviewActionException("That decision has already been undone.");
        db.ReviewDecisions.Remove(decision);
        var command = Command(decision.LookupKey, now);
        db.WorkerCommands.Add(command);
        await db.SaveChangesAsync(cancellationToken);
        return command;
    }

    /// <summary>
    /// Asks the Worker to search the catalogue and returns the search to wait on. An identical search that's still open
    /// is reused, and a new one is refused while <see cref="MaxOpenSearches"/> are waiting.
    /// </summary>
    public static async Task<Guid> RequestSearchAsync(AppDbContext db, string title, string? author, DateTime now, CancellationToken cancellationToken)
    {
        var cleanTitle = Clean(title, BookMention.MaxTitleLength) ?? throw new ReviewActionException("Enter a title to search for.");
        var cleanAuthor = Clean(author, BookMention.MaxAuthorLength);
        var open = await db.CatalogSearches.Where(s => s.CompletedAt == null).Select(s => new { s.Id, s.Title, s.Author }).ToListAsync(cancellationToken);
        if (open.FirstOrDefault(s => s.Title == cleanTitle && s.Author == cleanAuthor) is { } same)
        {
            return same.Id;
        }

        if (open.Count >= MaxOpenSearches)
        {
            throw new ReviewActionException("Too many searches are waiting already. Try again in a moment.");
        }

        var search = new CatalogSearch { Title = cleanTitle, Author = cleanAuthor, CreatedAt = now };
        db.CatalogSearches.Add(search);
        await db.SaveChangesAsync(cancellationToken);
        return search.Id;
    }

    private static async Task<WorkerCommand> SaveAsync(
        AppDbContext db, string key, string title, string? author, ReviewVerdict verdict, Guid? bookId, Func<Guid, CatalogBook?> pick,
        string? note, DateTime now, CancellationToken cancellationToken)
    {
        var decision = await db.ReviewDecisions.SingleOrDefaultAsync(d => d.LookupKey == key, cancellationToken);
        if (decision is null)
        {
            decision = new ReviewDecision { LookupKey = key, Title = title };
            db.ReviewDecisions.Add(decision);
        }

        decision.Title = title;
        decision.Author = author;
        decision.Verdict = verdict;
        decision.BookId = bookId;
        decision.Pick = pick(decision.Id);
        decision.Note = note;
        // A new DecidedAt is what tells the matcher that work it started under the old decision is stale.
        decision.DecidedAt = now;
        var command = Command(key, now);
        db.WorkerCommands.Add(command);
        await db.SaveChangesAsync(cancellationToken);
        return command;
    }

    private static CatalogBook? NoPick(Guid decisionId) => null;

    private static WorkerCommand Command(string key, DateTime now) =>
        new() { Type = WorkerCommandType.ReviewDecided, LookupKey = key, CreatedAt = now };

    /// <summary>
    /// The title and author of the newest mention with this key, to record on the decision. Decisions only go on keys
    /// the matcher actually stored, so a key the page made up, or one whose mentions have all gone, is refused.
    /// </summary>
    private static async Task<(string Title, string? Author)> MentionedAsync(AppDbContext db, string key, CancellationToken cancellationToken)
    {
        var mention = await db.BookMentions
            .Where(m => m.LookupKey == key)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Title, m.Author })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ReviewActionException("No mentions of this title are left. Refresh the list.");
        return (mention.Title, mention.Author);
    }

    /// <summary>
    /// Text a person typed becomes a stored book, so control characters, line and paragraph separators and invisible
    /// format characters (such as the bidi overrides that make text display reversed) become spaces.
    /// </summary>
    private static string? Typed(string? text) =>
        text is null ? null : string.Concat(text.Select(c => char.IsControl(c) || IsInvisible(c) ? ' ' : c));

    private static bool IsInvisible(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    private static string? Clean(string? text, int maxLength)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
