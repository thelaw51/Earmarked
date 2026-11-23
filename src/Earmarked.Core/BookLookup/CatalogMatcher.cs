using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Extraction;

namespace Earmarked.Core.BookLookup;

/// <summary>
/// Decides which catalogue entry, if any, is the book a mention names. Catalogue search is loose: it returns
/// omnibus editions, "summary of" knock-offs and unrelated books with similar words, and for a title that isn't a book
/// it still returns something. So a candidate must have the same title and, when the model named an author, an author
/// with the same surname. Titles come from speech-to-text, so "the same" is generous about form: case, punctuation,
/// articles, "and", hyphens and spacing ("Slaughterhouse Five", "Slaughterhouse-Five"), number words ("50", "Fifty"),
/// a subtitle and a bracketed edition note don't count. Surnames are compared against every author and every word of
/// each name, and allowed a few letters out (see <see cref="IsNearSurname"/>); a match that needed that leeway, or one where
/// the candidate's title only starts with the mention's, is flagged in <see cref="Doubts"/> so a person checks it.
/// With no author to confirm it, a one-edition book is too likely to be a coincidence ("my second book" is a real
/// title), so it needs at least <see cref="MinEditionsWithoutAuthor"/> editions, and a title made only of generic words
/// ("the book", "his new memoir") isn't looked up at all: see <see cref="IsTooVague"/>.
/// These rules only compare. <see cref="LookupKey"/> is stored and doesn't use them.
/// </summary>
public static partial class CatalogMatcher
{
    /// <summary>Stored on every match. Bump it when these rules or <see cref="Doubts"/> change, and every mention is matched again.</summary>
    public const string Version = "match-v6";

    public const int MinEditionsWithoutAuthor = 2;

    /// <summary>
    /// A match to a book with this many editions or fewer is flagged <see cref="MatchDoubts.FewEditions"/>. A catalogue
    /// that doesn't count editions sets no flag, and its books never pass <see cref="MinEditionsWithoutAuthor"/>.
    /// </summary>
    public const int FewEditions = 2;

    private static readonly HashSet<string> NameSuffixes = new(StringComparer.Ordinal) { "jr", "sr", "ii", "iii", "iv", "phd", "md" };

    // Words that describe a book without naming one. A title made only of these is a reference, not a title.
    private static readonly HashSet<string> GenericTitleWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "my", "his", "her", "their", "your", "our", "its", "this", "that", "these", "those",
        "new", "latest", "first", "second", "third", "next", "last", "previous", "other", "own", "whole", "upcoming", "forthcoming",
        "book", "books", "novel", "novels", "memoir", "memoirs", "biography", "autobiography", "story", "stories", "essay", "essays",
        "collection", "series", "sequel", "trilogy", "volume", "manuscript", "work", "piece", "text", "title", "chapter",
    };

    // Words in a title or author name that mark a derivative work: a summary or study aid, not the book itself.
    private static readonly string[] DerivativeMarkers = ["summary", "study guide", "workbook", "sparknotes", "cliffsnotes", "analysis of"];

    // Left out of title comparison: speech-to-text and catalogues disagree on these more than on any other words.
    private static readonly HashSet<string> IgnoredTitleWords = new(StringComparer.Ordinal) { "the", "a", "an", "and" };

    private static readonly Dictionary<string, int> SmallNumbers = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7,
        ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    private static readonly Dictionary<string, long> Scales = new(StringComparer.Ordinal) { ["thousand"] = 1_000, ["million"] = 1_000_000 };

    /// <summary>How a candidate's title matched the mention's, best first.</summary>
    private enum TitleMatch
    {
        /// <summary>The whole title, as written.</summary>
        Whole,

        /// <summary>The title or the part before its colon, against the candidate's title, main title or title with subtitle.</summary>
        Main,

        /// <summary>Only the part before a comma (<see cref="MainTitle"/>).</summary>
        BeforeComma,

        /// <summary>The candidate's title starts with the whole of the mention's and goes on: a subtitle without a colon.</summary>
        Prefix,

        None,
    }

    /// <summary>How a candidate's authors matched the mention's, best first.</summary>
    private enum AuthorMatch
    {
        /// <summary>Some author has the same surname.</summary>
        Same,

        /// <summary>A surname matched another word of a name, or only with letters out (<see cref="IsNearSurname"/>).</summary>
        Near,

        None,
    }

    /// <summary>
    /// The key two mentions share when they would get the same answer: the title key and the author's surname. Used to
    /// reuse an earlier result instead of asking the catalogue again, and to apply a <see cref="ReviewDecision"/>.
    /// Decisions are stored by this key, so changing what it returns (here, in <see cref="MentionMerger.TitleKey"/> or
    /// in <see cref="Surname"/>) needs a step that re-keys them from their stored title and author. The looser
    /// comparison <see cref="Pick"/> uses is deliberately not part of it.
    /// </summary>
    public static string LookupKey(string title, string? author) => $"{MentionMerger.TitleKey(title)}|{Surname(author)}";

    /// <summary>True when the author text yields a surname to check a candidate against.</summary>
    public static bool HasSurname(string? author) => Surname(author).Length > 0;

    /// <summary>
    /// The title without what looks like a subtitle, to search with when the whole title finds nothing: the part before
    /// the first colon, or, when the mention has an author surname to check, before the first comma. The model often
    /// writes "Title, Subtitle" where the catalogue has "Title: Subtitle" or only "Title", and a catalogue's title search
    /// finds nothing for the long form. Commas count only with a surname, because "Eats, Shoots &amp; Leaves" without one
    /// would be cut to a common word. Null when there is no such part.
    /// </summary>
    public static string? MainTitle(string title, string? author)
    {
        var cut = title.IndexOf(':', StringComparison.Ordinal);
        if (cut <= 0 && HasSurname(author))
        {
            cut = title.IndexOf(", ", StringComparison.Ordinal);
        }

        return cut > 0 && MentionMerger.TitleKey(title[..cut]).Length > 0 ? title[..cut].Trim() : null;
    }

    /// <summary>
    /// The candidate that is this book, or null if none qualifies. One matching the whole title beats one matching only
    /// the part before a subtitle, so "Dune Messiah: A Novel" finds Dune Messiah, not the more popular Dune; then one
    /// whose author has the very surname beats one that only came close; after that, most editions wins. The part
    /// before a comma counts as in <see cref="MainTitle"/>. A candidate whose title only starts with the mention's
    /// ("The Second Amendment" for "The Second Amendment in Law and History") counts only when an author matched.
    /// </summary>
    public static CatalogBook? Pick(string title, string? author, IReadOnlyList<CatalogBook> candidates)
    {
        if (IsTooVague(title, author))
        {
            return null;
        }

        var mention = new Mention(title, author);
        if (mention.Keys.Count == 0)
        {
            return null;
        }

        return candidates
            .Select(c => (Book: c, Title: mention.TitleMatchOf(c), Author: mention.AuthorMatchOf(c)))
            .Where(m => m.Title != TitleMatch.None && !IsDerivative(m.Book, title))
            .Where(m => mention.Surnames.Count == 0
                ? m.Title != TitleMatch.Prefix && m.Book.EditionCount >= MinEditionsWithoutAuthor
                : m.Title == TitleMatch.Prefix ? m.Author == AuthorMatch.Same : m.Author != AuthorMatch.None)
            .OrderBy(m => m.Title == TitleMatch.Whole ? 0 : m.Title == TitleMatch.Prefix ? 2 : 1)
            .ThenBy(m => m.Author)
            .ThenByDescending(m => m.Book.EditionCount)
            .Select(m => m.Book)
            .FirstOrDefault();
    }

    /// <summary>
    /// The last resort when <see cref="Pick"/> found nothing in any search: a candidate with the whole title and at
    /// least <see cref="MinEditionsWithoutAuthor"/> editions, whatever its author. Speech-to-text mangles names beyond
    /// any letter count ("Barchefskey" for "Barczewski"), and the model sometimes names the wrong author, so the book
    /// is likelier than not; but it may be another book with the same title, so <see cref="Doubts"/> always flags it
    /// <see cref="MatchDoubts.FuzzySurname"/>. Only for a mention with an author (without one, <see cref="Pick"/>
    /// already applies this rule) and never for a generic title.
    /// </summary>
    public static CatalogBook? PickDespiteAuthor(string title, string? author, IReadOnlyList<CatalogBook> candidates)
    {
        if (!HasSurname(author) || IsTooVague(title, null))
        {
            return null;
        }

        var mention = new Mention(title, author);
        return candidates
            .Where(c => mention.TitleMatchOf(c) == TitleMatch.Whole && !IsDerivative(c, title) && c.EditionCount >= MinEditionsWithoutAuthor)
            .OrderBy(mention.AuthorMatchOf)
            .ThenByDescending(c => c.EditionCount)
            .FirstOrDefault();
    }

    /// <summary>
    /// Why <paramref name="picked"/>, chosen by <see cref="Pick"/> or <see cref="PickDespiteAuthor"/> for this title
    /// and author, might still be the wrong book. The review queue shows matches with any doubt to a person.
    /// </summary>
    public static MatchDoubts Doubts(string title, string? author, CatalogBook picked)
    {
        var mention = new Mention(title, author);
        var doubts = picked.EditionCount <= FewEditions ? MatchDoubts.FewEditions : MatchDoubts.None;
        if (mention.TitleMatchOf(picked) is not (TitleMatch.Whole or TitleMatch.Main))
        {
            doubts |= MatchDoubts.ShortTitle;
        }

        if (mention.Surnames.Count == 0)
        {
            return doubts | MatchDoubts.NoAuthor;
        }

        return mention.AuthorMatchOf(picked) == AuthorMatch.Same ? doubts : doubts | MatchDoubts.FuzzySurname;
    }

    /// <summary>
    /// True for a title made only of generic words with no author to pin it down: the model has turned "the book" or
    /// "my new memoir" into a title. Catalogues do have books called "The Book", so with an author the title is still
    /// looked up and the surname decides.
    /// </summary>
    public static bool IsTooVague(string title, string? author) =>
        Surname(author).Length == 0
        && title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(QuoteLocator.Normalise).Where(w => w.Length > 0) is var words
        && words.Any()
        && words.All(GenericTitleWords.Contains);

    /// <summary>A mention's title and author, prepared once for comparing with each candidate.</summary>
    private sealed class Mention(string title, string? author)
    {
        /// <summary>The whole title's words, as <see cref="TitleWords"/> gives them.</summary>
        public List<string> Full { get; } = TitleWords(title);

        /// <summary>The whole title and the part before a colon, as keys.</summary>
        public HashSet<string> Keys { get; } = TitleKeys(title, null);

        /// <summary>The part before a colon or comma, when <see cref="MainTitle"/> allows it, as a key.</summary>
        public string? Comma { get; } = MainTitle(title, author) is { } main ? Key(TitleWords(main)) : null;

        /// <summary>Each named author's surname, and the parts of a double-barrelled one.</summary>
        public List<string> Surnames { get; } = [.. SplitAuthors(author).SelectMany(NameForms).Distinct(StringComparer.Ordinal)];

        /// <summary>Each named author's whole surname, the only forms that make <see cref="AuthorMatch.Same"/>.</summary>
        private HashSet<string> WholeSurnames { get; } = [.. SplitAuthors(author).SelectMany(WholeSurnameSpellings)];

        /// <summary>How well a candidate's title matches: see <see cref="TitleMatch"/>.</summary>
        public TitleMatch TitleMatchOf(CatalogBook candidate)
        {
            var keys = TitleKeys(candidate.Title, candidate.Subtitle);
            if (keys.Contains(Key(Full)))
            {
                return TitleMatch.Whole;
            }

            if (keys.Overlaps(Keys))
            {
                return TitleMatch.Main;
            }

            if (Comma is not null && keys.Contains(Comma))
            {
                return TitleMatch.BeforeComma;
            }

            // Only with two words or more: "Dune" would otherwise find every book whose title starts with it.
            var prefix = Full.Count >= 2 && TitleVariants(candidate.Title, candidate.Subtitle)
                .Select(TitleWords)
                .Any(words => words.Count > Full.Count && words.Take(Full.Count).SequenceEqual(Full, StringComparer.Ordinal));
            return prefix ? TitleMatch.Prefix : TitleMatch.None;
        }

        /// <summary>
        /// How well a candidate's authors match: the same surname, a near one through <see cref="IsNearSurname"/>
        /// against any word of any name, or none. Always none when the mention names no author.
        /// </summary>
        public AuthorMatch AuthorMatchOf(CatalogBook candidate)
        {
            if (Surnames.Count == 0)
            {
                return AuthorMatch.None;
            }

            // One part of a double-barrelled surname is only near: "Lawrence-Mathers" isn't "D. H. Lawrence".
            var names = candidate.Authors.SelectMany(SplitAuthors).ToList();
            if (names.SelectMany(WholeSurnameSpellings).Any(WholeSurnames.Contains))
            {
                return AuthorMatch.Same;
            }

            return names.SelectMany(NameWords).Any(word => Surnames.Any(surname => IsNearSurname(surname, word)))
                ? AuthorMatch.Near
                : AuthorMatch.None;
        }
    }

    /// <summary>
    /// The full title, the title before any colon, (for a candidate) title plus subtitle, and the title without a
    /// bracketed note ("The Life of the Mind (Combined 2 Volumes in 1)"), all as comparison keys.
    /// </summary>
    private static HashSet<string> TitleKeys(string title, string? subtitle)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in TitleVariants(title, subtitle))
        {
            keys.Add(Key(TitleWords(variant)));
            if (variant.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon)
            {
                keys.Add(Key(TitleWords(variant[..colon])));
            }
        }

        keys.Remove("");
        return keys;
    }

    /// <summary>The title, the title with its subtitle, and the title without bracketed notes, when those differ.</summary>
    private static IEnumerable<string> TitleVariants(string title, string? subtitle)
    {
        yield return title;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            yield return $"{title} {subtitle}";
        }

        if (Bracketed().Replace(title, " ") is var bare && bare != title)
        {
            yield return bare;
        }
    }

    /// <summary>
    /// A title's words for comparison: split at spaces and hyphens, lower case, letters and digits only, number words as
    /// digits ("Four Thousand" as "4000", like "4,000"), and without "the", "a", "an" or "and" (unless that leaves nothing).
    /// </summary>
    private static List<string> TitleWords(string title)
    {
        var words = Numbers(title.Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(QuoteLocator.Normalise)
            .Where(w => w.Length > 0)
            .ToList());
        var kept = words.Where(w => !IgnoredTitleWords.Contains(w)).ToList();
        return kept.Count > 0 ? kept : words;
    }

    /// <summary>
    /// The comparison key: the words run together, so "First Born" and "Firstborn" are the same title. Only the whole
    /// title is compared this way, and the words still have to come in the same order.
    /// </summary>
    private static string Key(List<string> words) => string.Concat(words);

    /// <summary>Replaces each run of number words with its value: "fifty" is "50", "one hundred and one" is "101".</summary>
    private static List<string> Numbers(List<string> words)
    {
        var result = new List<string>(words.Count);
        for (var i = 0; i < words.Count;)
        {
            if (!IsNumberWord(words[i]))
            {
                result.Add(words[i++]);
                continue;
            }

            long total = 0, current = 0;
            bool units = false, tens = false;
            while (i < words.Count)
            {
                var word = words[i];
                if (SmallNumbers.TryGetValue(word, out var small) && !units && (!tens || small < 10))
                {
                    current += small;
                    units = true;
                }
                else if (Tens.TryGetValue(word, out var ten) && !units && !tens)
                {
                    current += ten;
                    tens = true;
                }
                else if (word == "hundred" && current < 100)
                {
                    current = (current == 0 ? 1 : current) * 100;
                    units = tens = false;
                }
                else if (Scales.TryGetValue(word, out var scale))
                {
                    total += (current == 0 ? 1 : current) * scale;
                    current = 0;
                    units = tens = false;
                }
                else if (word != "and" || units || tens || total + current == 0 || i + 1 == words.Count
                    || !(SmallNumbers.ContainsKey(words[i + 1]) || Tens.ContainsKey(words[i + 1])))
                {
                    // Not a number word, or one that can't follow ("one two"). "And" belongs to the number only after a
                    // hundred or thousand and before what follows it: "one hundred and one".
                    break;
                }

                i++;
            }

            result.Add((total + current).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return result;
    }

    /// <summary>True for a word <see cref="Numbers"/> reads as part of a number.</summary>
    private static bool IsNumberWord(string word) =>
        SmallNumbers.ContainsKey(word) || Tens.ContainsKey(word) || word == "hundred" || Scales.ContainsKey(word);

    /// <summary>
    /// The names in an author string: "Kittel and Kroemer", "Kittel &amp; Kroemer" and "Charles Kittel, Herbert
    /// Kroemer" are two. A comma splits only when every part is a full name of two words or more, so "Tolkien, J.R.R."
    /// and "Ines Calder, Jr." stay one name each.
    /// </summary>
    private static IEnumerable<string> SplitAuthors(string? author)
    {
        if (string.IsNullOrWhiteSpace(author))
        {
            yield break;
        }

        foreach (var part in AuthorSeparator().Split(author).Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var names = part.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length > 1 && names.All(IsFullName))
            {
                foreach (var name in names)
                {
                    yield return name;
                }
            }
            else
            {
                yield return part;
            }
        }
    }

    /// <summary>True for two words or more ending in a surname, not an initial or a suffix: "Herbert Kroemer", not "J.R.R.".</summary>
    private static bool IsFullName(string name) =>
        name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(QuoteLocator.Normalise).ToList() is var words
        && words.Count >= 2
        && words[^1].Length >= 2
        && !NameSuffixes.Contains(words[^1]);

    /// <summary>
    /// A name's surname without accents, plus each part of a double-barrelled one: "Lawrence-Mathers" is also
    /// "lawrence" and "mathers", and "Márquez" is "marquez". A part only ever makes a near match.
    /// </summary>
    private static IEnumerable<string> NameForms(string name)
    {
        var whole = WholeSurname(name);
        if (whole.Length == 0)
        {
            yield break;
        }

        yield return whole;
        foreach (var part in NameWords(SurnameWord(name) ?? ""))
        {
            yield return part;
        }
    }

    /// <summary>A name's surname without accents, or empty when it has none.</summary>
    private static string WholeSurname(string name) => Surname(name) is { Length: > 0 } surname ? WithoutAccents(surname) : "";

    /// <summary>
    /// The ways a name's whole surname may be written: <see cref="WholeSurname"/>, and for a name of three words or
    /// more also its last two run together, because speech-to-text drops the hyphen ("Anne Lawrence Mathers" for
    /// "Anne Lawrence-Mathers"). One part alone is never a whole surname.
    /// </summary>
    private static IEnumerable<string> WholeSurnameSpellings(string name)
    {
        var whole = WholeSurname(name);
        if (whole.Length == 0)
        {
            yield break;
        }

        yield return whole;
        var words = SurnamePart(name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => WithoutAccents(QuoteLocator.Normalise(w)))
            .Where(w => w.Length > 0 && !NameSuffixes.Contains(w))
            .ToList();
        if (words.Count >= 3)
        {
            yield return words[^2] + words[^1];
        }
    }

    /// <summary>
    /// Every word of a name of two letters or more, without accents and split at hyphens too, skipping initials and
    /// suffixes.
    /// </summary>
    private static IEnumerable<string> NameWords(string name) =>
        name.Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => WithoutAccents(QuoteLocator.Normalise(w)))
            .Where(w => w.Length >= 2 && !NameSuffixes.Contains(w));

    /// <summary>The word with accents removed, so "Márquez" compares equal to "Marquez".</summary>
    private static string WithoutAccents(string word)
    {
        var decomposed = word.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// A summary or study aid rather than the book. A marker the mention's own title contains doesn't count, so a book
    /// really called "An Analysis of Algorithms" can still match.
    /// </summary>
    private static bool IsDerivative(CatalogBook candidate, string mentioned) =>
        DerivativeMarkers.Where(marker => !mentioned.Contains(marker, StringComparison.OrdinalIgnoreCase)).Any(marker =>
            candidate.Title.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || candidate.Authors.Any(a => a.Contains(marker, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// True when a word of a candidate's name is close enough to a mention's surname to be the same name misheard:
    /// equal; one the start of the other, four letters or more and at most <see cref="MaxSurnamePrefixGap"/> letters
    /// short ("Roos" for "Roose", "Burke" for "Burkeman", not "King" for "Kingsolver"); or, after
    /// folding letters that sound alike (c, k and q; v and f; z and s; ph and f; doubled letters), one letter added or
    /// dropped for names of four letters or more ("Rose" for "Roose", but not "Rene" for "Rena"), one letter out for
    /// five letters or more ("Elliot" for "Eliot"), or two out for seven letters or more when the first letter agrees
    /// ("Copeland" for "Coupland", but not "Anderson" for "Henderson"). Matches through this always carry
    /// <see cref="MatchDoubts.FuzzySurname"/>.
    /// </summary>
    private static bool IsNearSurname(string surname, string word)
    {
        if (surname == word)
        {
            return true;
        }

        var (shorter, longer) = surname.Length <= word.Length ? (surname, word) : (word, surname);
        if (shorter.Length >= 4 && longer.Length - shorter.Length <= MaxSurnamePrefixGap && longer.StartsWith(shorter, StringComparison.Ordinal))
        {
            return true;
        }

        // Four letters allow only an added or dropped letter, compared unfolded: folding collapses "rr", which would
        // turn "Lohr" against "Lorr", a changed letter, into a dropped one.
        if (shorter.Length == 4)
        {
            return longer.Length == 5 && EditDistance(shorter, longer, 1) == 1;
        }

        if (shorter.Length < 4)
        {
            return false;
        }

        var (a, b) = (Fold(surname), Fold(word));
        var distance = EditDistance(a, b, 2);
        return distance <= 1 || (distance == 2 && shorter.Length >= 7 && a[0] == b[0]);
    }

    /// <summary>
    /// The most letters a surname may run past a misheard start of it ("Burke" for "Burkeman"), so "King" isn't
    /// "Kingsolver".
    /// </summary>
    private const int MaxSurnamePrefixGap = 3;

    /// <summary>Spells alike what speech-to-text hears alike, for <see cref="IsNearSurname"/> only.</summary>
    private static string Fold(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == 'h' && i > 0 && name[i - 1] == 'p')
            {
                continue;
            }

            var c = name[i] switch
            {
                'c' or 'q' => 'k',
                'v' => 'f',
                'z' => 's',
                'p' when i + 1 < name.Length && name[i + 1] == 'h' => 'f',
                var other => other,
            };

            if (builder.Length == 0 || builder[^1] != c)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>Levenshtein distance between two short words, or <paramref name="limit"/> + 1 once it is past that.</summary>
    private static int EditDistance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                best = Math.Min(best, current[j]);
            }

            // Every later row is at least this row's minimum, so the answer is already past the limit.
            if (best > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return Math.Min(previous[b.Length], limit + 1);
    }

    /// <summary>
    /// The surname, normalised: the last word of the name, or the part before a comma for "Tolkien, J.R.R.", skipping
    /// suffixes. "J.R.R. Tolkien", "Tolkien, J. R. R." and "Tolkien" all give "tolkien"; "Martin Luther King Jr." gives "king".
    /// Part of <see cref="LookupKey"/>: never change what it returns without re-keying stored decisions.
    /// </summary>
    private static string Surname(string? name) => SurnameWord(name) is { } word ? QuoteLocator.Normalise(word) : "";

    /// <summary>The word <see cref="Surname"/> comes from, as written, or null when there is none.</summary>
    private static string? SurnameWord(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return SurnamePart(name).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(w => QuoteLocator.Normalise(w) is { Length: > 0 } normalised && !NameSuffixes.Contains(normalised));
    }

    /// <summary>The part of a name that ends in its surname: "Surname, Given" puts the surname first; "Given Surname, Jr." only looks like it.</summary>
    private static string SurnamePart(string name)
    {
        var parts = name.Split(',', 2);
        return parts.Length == 2 && !NameSuffixes.Contains(QuoteLocator.Normalise(parts[1])) ? parts[0] : name;
    }

    private static readonly char[] TitleSeparators = [' ', '\t', '\r', '\n', '-', '‐', '‑', '‒', '–', '—'];

    [GeneratedRegex(@"\s+(?:and|&)\s+|\s*[&;]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorSeparator();

    [GeneratedRegex(@"\s*[(\[][^)\]]*[)\]]\s*")]
    private static partial Regex Bracketed();

    /// <summary>
    /// A picked book was copied from a search result or built from what a person typed, and it sat in the database in
    /// between. Before it becomes a <see cref="Book"/>, check what every entry must have: a known provider, an ID, a
    /// title, a link on that catalogue's own host and, if it has a cover, a cover from that catalogue's image host
    /// (see <see cref="IsSafeLink"/>). A <see cref="BookLookupProviders.Manual"/> entry is the one kind with no links,
    /// and its ID must be a GUID (its decision's). The review page checks a pick with this when it's made, and the matcher again
    /// before adding it. The record's types say most of these aren't null, but JSON read back from the database can
    /// still hold nulls.
    /// </summary>
    public static bool IsUsable(CatalogBook? pick) =>
        pick is not null
        && !string.IsNullOrWhiteSpace(pick.CatalogId) && pick.CatalogId.Length <= MaxCatalogIdLength
        && !string.IsNullOrWhiteSpace(pick.Title)
        && pick.Authors is not null && pick.Authors.All(a => a is not null)
        && pick.ProviderOrDefault switch
        {
            BookLookupProviders.Manual => pick.InfoUrl is null && pick.CoverUrl is null && Guid.TryParseExact(pick.CatalogId, "N", out _),
            var provider => BookLookupProviders.Known.Contains(provider)
                && IsSafeLink(pick.InfoUrl, BookLookupProviders.InfoHost(provider))
                && (pick.CoverUrl is null || IsSafeLink(pick.CoverUrl, BookLookupProviders.CoverHost(provider), allowSubdomains: true)),
        };

    /// <summary>
    /// True for an https link of a storable length on <paramref name="host"/> (or, with <paramref name="allowSubdomains"/>,
    /// a subdomain of it), on the default port and with no user info. The parsed host is compared, so
    /// <c>evilmzstatic.com</c> and <c>mzstatic.com.example</c> don't pass for <c>mzstatic.com</c>. Adapters use it too, so
    /// what they keep and what this accepts can't drift apart.
    /// </summary>
    public static bool IsSafeLink(string? url, string? host, bool allowSubdomains = false) =>
        url is not null && host is not null && url.Length <= Book.MaxUrlLength
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)
            || (allowSubdomains && uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The link in the form it will be stored (<see cref="Uri.AbsoluteUri"/>, with non-ASCII characters escaped) if
    /// <see cref="IsSafeLink"/> accepts that form, else null. Adapters keep covers through this, because escaping can
    /// lengthen a link past <see cref="Book.MaxUrlLength"/> after the raw text passed the check.
    /// </summary>
    public static string? SafeLinkOrNull(string? url, string? host, bool allowSubdomains = false) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsSafeLink(uri.AbsoluteUri, host, allowSubdomains)
            ? uri.AbsoluteUri
            : null;

    private const int MaxCatalogIdLength = 40;
}
