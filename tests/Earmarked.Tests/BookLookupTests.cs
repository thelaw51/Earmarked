using System.ComponentModel.DataAnnotations;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;
using Earmarked.Core.Providers.BookLookup;
using Earmarked.Core.Providers.BookLookup.OpenLibrary;

namespace Earmarked.Tests;

public class BookLookupTests
{
    private static CatalogBook Book(string title, string author, int editions = 2, string? subtitle = null, string id = "OL1W") =>
        new(id, title, subtitle, [author], null, editions, $"https://openlibrary.org/works/{id}", null);

    [Theory]
    [InlineData("the lighthouse keepers almanac", null)]
    [InlineData("Lighthouse Keeper's Almanac", "Ines Calder")]
    [InlineData("The Lighthouse Keeper's Almanac: A Novel", "I. Calder")]
    public void Picks_the_same_title_ignoring_case_punctuation_articles_and_subtitles(string title, string? author) =>
        Assert.Equal("OL1W", CatalogMatcher.Pick(title, author, [Book("The Lighthouse Keeper's Almanac", "Ines Calder")])?.CatalogId);

    [Theory]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("ab\U0001F4DAcd", 3, "ab")]
    [InlineData("ab\U0001F4DAcd", 4, "ab\U0001F4DA")]
    [InlineData("\U0001F4DA", 1, "")]
    [InlineData("", 0, "")]
    public void Capping_text_never_splits_a_surrogate_pair(string value, int max, string expected)
    {
        var capped = CatalogText.Cap(value, max);

        Assert.Equal(expected, capped);
        Assert.False(capped.Length > 0 && char.IsHighSurrogate(capped[^1]));
    }

    [Fact]
    public void A_link_is_checked_in_the_form_it_will_be_stored()
    {
        // 250 characters as written, but each "é" becomes "%C3%A9" when escaped, taking it past the 300 limit.
        var lengthened = "https://assets.hardcover.app/" + new string('é', 221);
        Assert.True(lengthened.Length <= Earmarked.Core.Data.Book.MaxUrlLength);

        Assert.Null(CatalogMatcher.SafeLinkOrNull(lengthened, "hardcover.app", allowSubdomains: true));
        Assert.Equal("https://assets.hardcover.app/caf%C3%A9.jpg", CatalogMatcher.SafeLinkOrNull("https://assets.hardcover.app/café.jpg", "hardcover.app", allowSubdomains: true));
        Assert.Null(CatalogMatcher.SafeLinkOrNull("https://evilhardcover.app/a.jpg", "hardcover.app", allowSubdomains: true));
        Assert.Null(CatalogMatcher.SafeLinkOrNull("not a link", "hardcover.app"));
    }

    [Theory]
    [InlineData("For the Love of Men, From Toxic to a More Mindful Masculinity", "Liz Plank", "For the Love of Men")]
    [InlineData("Small Rivers, A Walking Year, Revised", "Tomas Reyne", "Small Rivers")]
    [InlineData("Small Rivers: A Walking Year", null, "Small Rivers")]
    [InlineData("Small Rivers: A Walking Year", "Tomas Reyne", "Small Rivers")]
    [InlineData("Eats, Shoots & Leaves", null, null)]
    [InlineData("Small Rivers", "Tomas Reyne", null)]
    [InlineData(", A Walking Year", "Tomas Reyne", null)]
    public void The_main_title_is_before_a_colon_or_with_an_author_a_comma(string title, string? author, string? expected) =>
        Assert.Equal(expected, CatalogMatcher.MainTitle(title, author));

    [Fact]
    public void A_comma_subtitle_matches_the_short_title_with_an_author_and_is_flagged()
    {
        var book = Book("For the Love of Men", "Liz Plank", editions: 3);
        const string title = "For the Love of Men, From Toxic to a More Mindful Masculinity";

        Assert.Same(book, CatalogMatcher.Pick(title, "Liz Plank", [book]));
        Assert.Equal(MatchDoubts.ShortTitle, CatalogMatcher.Doubts(title, "Liz Plank", book));
        Assert.Null(CatalogMatcher.Pick(title, null, [book]));
    }

    [Fact]
    public void A_book_with_the_whole_title_beats_one_matching_before_the_comma()
    {
        var shortOne = Book("Eats", "Lynne Truss", editions: 40, id: "OL2W");
        var whole = Book("Eats, Shoots & Leaves", "Lynne Truss", editions: 5, id: "OL3W");

        var picked = CatalogMatcher.Pick("Eats, Shoots & Leaves", "Lynne Truss", [shortOne, whole]);

        Assert.Same(whole, picked);
        Assert.Equal(MatchDoubts.None, CatalogMatcher.Doubts("Eats, Shoots & Leaves", "Lynne Truss", whole));
    }

    [Fact]
    public void A_catalogue_subtitle_matching_the_comma_part_needs_no_flag() =>
        Assert.Equal(MatchDoubts.None, CatalogMatcher.Doubts("Small Rivers, A Walking Year", "Tomas Reyne",
            Book("Small Rivers", "Tomas Reyne", editions: 3, subtitle: "A Walking Year")));

    [Fact]
    public void A_candidate_with_a_subtitle_matches_the_full_title_too() =>
        Assert.NotNull(CatalogMatcher.Pick("Small Rivers: A Walking Year", null, [Book("Small Rivers", "Tomas Reyne", subtitle: "A Walking Year")]));

    [Fact]
    public void Rejects_a_candidate_with_a_different_title()
    {
        // Catalogue search returns something for almost anything, including phrases that aren't books.
        Assert.Null(CatalogMatcher.Pick("The Harbour Case", null, [Book("Harbour Law and the Case Method", "A Scholar")]));
        Assert.Null(CatalogMatcher.Pick("my second book", null, [Book("My Second Book of Colours", "A Child")]));
    }

    [Fact]
    public void Rejects_a_candidate_by_another_author_when_the_model_named_one() =>
        Assert.Null(CatalogMatcher.Pick("Small Rivers", "Ines Calder", [Book("Small Rivers", "Tomas Reyne")]));

    [Theory]
    [InlineData("George Elliot", true)]
    [InlineData("G. Eliott", true)]
    [InlineData("George Eliot", true)]
    [InlineData("George Elton", false)]
    public void Allows_one_letter_out_in_a_longer_surname(string author, bool matches) =>
        Assert.Equal(matches, CatalogMatcher.Pick("Middlemarch", author, [Book("Middlemarch", "George Eliot")]) is not null);

    [Fact]
    public void Short_surnames_must_match_exactly() =>
        Assert.Null(CatalogMatcher.Pick("Small Rivers", "Tomas Rene", [Book("Small Rivers", "Tomas Rena")]));

    [Fact]
    public void Without_an_author_a_one_edition_book_is_not_enough()
    {
        // "my second book" is a real one-edition title: without an author, an exact title alone is too weak.
        Assert.Null(CatalogMatcher.Pick("My Second Book", null, [Book("My second book", "Someone", editions: 1)]));
        Assert.NotNull(CatalogMatcher.Pick("My Second Book", "Someone", [Book("My second book", "Someone", editions: 1)]));
    }

    [Fact]
    public void A_whole_title_match_beats_a_more_popular_book_matching_only_the_part_before_the_subtitle()
    {
        var picked = CatalogMatcher.Pick("Small Rivers: The Delta Years", "Tomas Reyne",
        [
            Book("Small Rivers", "Tomas Reyne", editions: 40, id: "OL1W"),
            Book("Small Rivers: The Delta Years", "Tomas Reyne", editions: 3, id: "OL2W"),
        ]);

        Assert.Equal("OL2W", picked?.CatalogId);
    }

    [Fact]
    public void A_book_whose_own_title_has_a_derivative_word_can_still_match() =>
        Assert.NotNull(CatalogMatcher.Pick("An Analysis of Tides", "Ines Calder", [Book("An Analysis of Tides", "Ines Calder")]));

    [Theory]
    [InlineData("Calder, Ines")]
    [InlineData("Ines Calder Jr.")]
    [InlineData("Ines Calder, Jr.")]
    [InlineData("Calder")]
    public void Reads_the_surname_from_reversed_names_and_names_with_suffixes(string author) =>
        Assert.NotNull(CatalogMatcher.Pick("Small Rivers", author, [Book("Small Rivers", "Ines Calder")]));

    [Fact]
    public void Suffixes_alone_dont_make_two_authors_share_a_lookup() =>
        Assert.NotEqual(CatalogMatcher.LookupKey("Small Rivers", "Ines Calder Jr."), CatalogMatcher.LookupKey("Small Rivers", "Tomas Reyne Jr."));

    [Theory]
    [InlineData("The Book")]
    [InlineData("the book")]
    [InlineData("My New Book")]
    [InlineData("his second novel")]
    [InlineData("Her memoir")]
    public void A_generic_reference_with_no_author_is_not_a_title(string title)
    {
        Assert.True(CatalogMatcher.IsTooVague(title, null));
        Assert.Null(CatalogMatcher.Pick(title, null, [Book(title, "Hungry Minds", editions: 50)]));
    }

    [Fact]
    public void A_generic_title_still_matches_when_the_author_confirms_it()
    {
        Assert.False(CatalogMatcher.IsTooVague("The Book", "Alan Watts"));
        Assert.NotNull(CatalogMatcher.Pick("The Book", "Alan Watts", [Book("The Book", "Alan Watts", editions: 30)]));
        Assert.Null(CatalogMatcher.Pick("The Book", "Alan Watts", [Book("The Book", "Hungry Minds", editions: 30)]));
    }

    [Theory]
    [InlineData("The Second Book of Tides")]
    [InlineData("Small Rivers")]
    [InlineData("Novel")]
    public void A_title_with_any_specific_word_is_not_generic(string title) =>
        Assert.Equal(title == "Novel", CatalogMatcher.IsTooVague(title, null));

    [Fact]
    public void Skips_summaries_and_prefers_the_work_with_most_editions()
    {
        var picked = CatalogMatcher.Pick("Small Rivers", null,
        [
            Book("Small Rivers", "Summary Press", editions: 9, id: "OL5W"),
            Book("Small Rivers", "Tomas Reyne", editions: 2, id: "OL6W"),
            Book("Small Rivers", "Tomas Reyne", editions: 7, id: "OL7W"),
        ]);

        Assert.Equal("OL7W", picked?.CatalogId);
    }

    [Fact]
    public void Lookup_keys_ignore_title_formatting_and_all_but_the_surname() =>
        Assert.Equal(
            CatalogMatcher.LookupKey("The Lighthouse Keeper's Almanac", "Ines Calder"),
            CatalogMatcher.LookupKey("lighthouse keepers almanac", "I. CALDER"));

    [Theory]
    [InlineData("you@example.com", "https://openlibrary.org", true)]
    [InlineData(null, "https://openlibrary.org", true)]
    [InlineData("you@example.com\r\nX-Evil: 1", "https://openlibrary.org", false)]
    [InlineData("you@example.com) (other", "https://openlibrary.org", false)]
    [InlineData("you@example.com", "http://openlibrary.org", false)]
    public void Open_library_settings_allow_only_a_plain_email_and_an_https_url(string? email, string baseUrl, bool valid)
    {
        var options = new OpenLibraryOptions { ContactEmail = email, BaseUrl = baseUrl };

        Assert.Equal(valid, !options.Validate(new ValidationContext(options)).Any());
    }

    [Fact]
    public void Book_lookup_is_on_with_open_library_by_default_and_rejects_unknown_providers()
    {
        Assert.True(new BookLookupOptions().Enabled);
        var unknown = new BookLookupOptions { Provider = "Nope" };
        Assert.NotEmpty(unknown.Validate(new ValidationContext(unknown)));
    }

    [Theory]
    [InlineData(null, 4, MatchDoubts.NoAuthor)]
    [InlineData(null, 2, MatchDoubts.NoAuthor | MatchDoubts.FewEditions)]
    [InlineData("Ines Calder", 4, MatchDoubts.None)]
    [InlineData("I. Calder", 1, MatchDoubts.FewEditions)]
    [InlineData("Ines Caldor", 4, MatchDoubts.FuzzySurname)]
    public void Flags_why_a_match_might_be_wrong(string? author, int editions, MatchDoubts expected) =>
        Assert.Equal(expected, CatalogMatcher.Doubts("The Lighthouse Keeper's Almanac", author, Book("The Lighthouse Keeper's Almanac", "Ines Calder", editions)));

    /// <summary>
    /// Mentions as speech-to-text and the model wrote them, against the book a person picked. Each row also says which
    /// rule found it: <see cref="CatalogMatcher.Pick"/>, or only <see cref="CatalogMatcher.PickDespiteAuthor"/> once
    /// every search missed, and the doubts that send a near miss to review.
    /// </summary>
    [Theory]
    [InlineData("50 Shades of Grey", null, "Fifty Shades of Grey", "E.L. James", true, MatchDoubts.NoAuthor)]
    [InlineData("Seven Powers", "Hamilton Helmer", "7 Powers", "Hamilton Helmer", true, MatchDoubts.None)]
    [InlineData("4,000 Weeks", "Oliver Burke", "Four Thousand Weeks", "Oliver Burkeman", true, MatchDoubts.FuzzySurname)]
    [InlineData("One Hundred Years of Solitude", "Gabriel Garcia Marquez", "100 Years of Solitude", "Gabriel García Márquez", true, MatchDoubts.None)]
    [InlineData("Catch Twenty-Two", "Joseph Heller", "Catch-22", "Joseph Heller", true, MatchDoubts.None)]
    [InlineData("Slaughterhouse Five", "Kurt Vonnegut", "Slaughterhouse-Five", "Kurt Vonnegut", true, MatchDoubts.None)]
    [InlineData("First Born Daughter", null, "Firstborn Daughter", "Katherine Ryan", true, MatchDoubts.NoAuthor)]
    [InlineData("Lessons from the Default Parent", null, "Lessons from a Default Parent", "Lou Beckett", true, MatchDoubts.NoAuthor)]
    [InlineData("Apple in China", null, "Apple in China", "Patrick McGee", true, MatchDoubts.NoAuthor)]
    [InlineData("Life of the Mind", "Hannah Arendt", "The Life of the Mind (Combined 2 Volumes in 1)", "Hannah Arendt", true, MatchDoubts.None)]
    [InlineData("The True History of Merlin, the Magician", "Anne Lawrence Mathers", "The True History of Merlin the Magician", "Anne Lawrence-Mathers", true, MatchDoubts.None)]
    [InlineData("Thermal Physics", "Kittel and Cromer", "Thermal Physics", "Charles Kittel, Herbert Kroemer", true, MatchDoubts.None)]
    [InlineData("The Second Amendment", "Carl Bogus", "The Second Amendment in law and history", "Carl T. Bogus", true, MatchDoubts.ShortTitle)]
    [InlineData("On Being the Right Size", "J.B.S. Haldane", "On being the right size and other essays", "J. B. S. Haldane", true, MatchDoubts.ShortTitle)]
    [InlineData("Generation X", "Douglas Copeland", "Generation X", "Douglas Coupland", true, MatchDoubts.FuzzySurname)]
    [InlineData("Seinfeldia", "Jennifer Keishan", "Seinfeldia", "Jennifer Keishin Armstrong", true, MatchDoubts.FuzzySurname)]
    [InlineData("The AGI Chronicles", "Kevin Roos", "The AGI Chronicles", "Kevin Roose", true, MatchDoubts.FuzzySurname)]
    [InlineData("AGI Chronicles, Inside the Global Pursuit of Artificial Superintelligence", "K. Rose", "The AGI Chronicles", "Kevin Roose", true, MatchDoubts.FuzzySurname | MatchDoubts.ShortTitle)]
    [InlineData("The Outer Limits of Reason", "Nosen Y anovsky", "The Outer Limits of Reason", "Noson S. Yanofsky", true, MatchDoubts.FuzzySurname)]
    [InlineData("Iron Curtain", "Anne Appenbaum", "Iron Curtain", "Anne Applebaum", true, MatchDoubts.FuzzySurname)]
    [InlineData("Heroic Failure and the British", "Stephanie Barchefskey", "Heroic Failure and the British", "Stephanie Barczewski", false, MatchDoubts.FuzzySurname)]
    [InlineData("The Silent Patient", "Alex Michelin", "The Silent Patient", "Alex Michaelides", false, MatchDoubts.FuzzySurname)]
    [InlineData("The Discovery of Dynamics", "Julian Barber", "The Discovery of Dynamics", "Julian Barbour", false, MatchDoubts.FuzzySurname)]
    [InlineData("The Secret Life of Groceries", "Benjamin Lohr", "The Secret Life of Groceries", "Benjamin Lorr", false, MatchDoubts.FuzzySurname)]
    [InlineData("Where Does My Stuff Come From?", "Willy Shi", "Where Does My Stuff Come From?", "Willy Shih", false, MatchDoubts.FuzzySurname)]
    public void Finds_books_through_transcription_errors(string title, string? author, string bookTitle, string bookAuthor, bool byPick, MatchDoubts doubts)
    {
        var book = Book(bookTitle, bookAuthor, editions: 10);

        Assert.Equal(byPick, CatalogMatcher.Pick(title, author, [book]) is not null);
        Assert.Same(book, CatalogMatcher.Pick(title, author, [book]) ?? CatalogMatcher.PickDespiteAuthor(title, author, [book]));
        Assert.Equal(doubts, CatalogMatcher.Doubts(title, author, book));
    }

    [Theory]
    [InlineData("Anderson", "Henderson")]
    [InlineData("Martin", "Morton")]
    [InlineData("Barnes", "Burns")]
    [InlineData("Shi", "Shih")]
    [InlineData("Lohr", "Lorr")]
    [InlineData("Elton", "Eliot")]
    [InlineData("Calder", "Reyne")]
    public void Clearly_different_surnames_still_dont_match(string mentioned, string actual) =>
        Assert.Null(CatalogMatcher.Pick("Small Rivers", $"Ines {mentioned}", [Book("Small Rivers", $"Ines {actual}", editions: 10)]));

    [Fact]
    public void A_first_name_counts_only_as_a_near_match()
    {
        var book = Book("Small Rivers", "Calder Reyne", editions: 10);

        Assert.Same(book, CatalogMatcher.Pick("Small Rivers", "Ines Calder", [book]));
        Assert.Equal(MatchDoubts.FuzzySurname, CatalogMatcher.Doubts("Small Rivers", "Ines Calder", book));
    }

    [Fact]
    public void The_exact_surname_beats_a_near_one_with_more_editions()
    {
        var near = Book("Middlemarch", "George Elliot", editions: 40, id: "OL1W");
        var exact = Book("Middlemarch", "George Eliot", editions: 3, id: "OL2W");

        Assert.Same(exact, CatalogMatcher.Pick("Middlemarch", "George Eliot", [near, exact]));
    }

    [Theory]
    [InlineData("The Silent Patient", "The Silent Patients")]
    [InlineData("Generation X", "Generation Xers")]
    [InlineData("Small Rivers", "Rivers Small")]
    [InlineData("Small Rivers", "Smallrivers Press Catalogue")]
    public void Different_titles_still_dont_match(string title, string other) =>
        Assert.Null(CatalogMatcher.Pick(title, "Ines Calder", [Book(other, "Ines Calder", editions: 10)]));

    [Fact]
    public void A_title_that_only_starts_the_candidates_needs_two_words_and_an_author()
    {
        var messiah = Book("Dune Messiah", "Frank Herbert", editions: 30);
        var essays = Book("On Being the Right Size and Other Essays", "J. B. S. Haldane", editions: 10);

        Assert.Null(CatalogMatcher.Pick("Dune", "Frank Herbert", [messiah]));
        Assert.Null(CatalogMatcher.Pick("On Being the Right Size", null, [essays]));
        Assert.Null(CatalogMatcher.Pick("On Being the Right Size", "Tomas Reyne", [essays]));
        Assert.Same(essays, CatalogMatcher.Pick("On Being the Right Size", "Haldane", [essays]));
    }

    [Fact]
    public void A_whole_title_beats_one_that_only_starts_with_it()
    {
        var longer = Book("The Second Amendment in Law and History", "Carl T. Bogus", editions: 30, id: "OL1W");
        var whole = Book("The Second Amendment", "Carl T. Bogus", editions: 3, id: "OL2W");

        Assert.Same(whole, CatalogMatcher.Pick("The Second Amendment", "Carl Bogus", [longer, whole]));
    }

    [Fact]
    public void Summaries_are_still_rejected_however_the_match_was_made()
    {
        var summary = Book("Four Thousand Weeks", "Quick Summary Books", editions: 9);
        var studyGuide = Book("Four Thousand Weeks: Summary and Analysis", "Oliver Burkeman", editions: 9);

        Assert.Null(CatalogMatcher.Pick("4,000 Weeks", "Oliver Burke", [summary, studyGuide]));
        Assert.Null(CatalogMatcher.PickDespiteAuthor("4,000 Weeks", "Oliver Burke", [summary, studyGuide]));
    }

    [Fact]
    public void Picking_despite_the_author_needs_an_author_the_whole_title_editions_and_a_specific_title()
    {
        Assert.Null(CatalogMatcher.PickDespiteAuthor("Small Rivers", null, [Book("Small Rivers", "Tomas Reyne", editions: 10)]));
        Assert.Null(CatalogMatcher.PickDespiteAuthor("Small Rivers", "Ines Calder", [Book("Small Rivers", "Tomas Reyne", editions: 1)]));
        Assert.Null(CatalogMatcher.PickDespiteAuthor("Small Rivers", "Ines Calder", [Book("Small Rivers", "Tomas Reyne") with { EditionCount = null }]));
        Assert.Null(CatalogMatcher.PickDespiteAuthor("Small Rivers, A Walking Year", "Ines Calder", [Book("Small Rivers", "Tomas Reyne", editions: 10)]));
        Assert.Null(CatalogMatcher.PickDespiteAuthor("The Book", "Alan Watts", [Book("The Book", "Hungry Minds", editions: 30)]));
        Assert.NotNull(CatalogMatcher.PickDespiteAuthor("Small Rivers", "Ines Calder", [Book("Small Rivers", "Tomas Reyne", editions: 2)]));
    }

    [Fact]
    public void A_one_edition_book_with_no_author_is_still_not_enough_however_long_the_title() =>
        Assert.Null(CatalogMatcher.Pick("Walter the Farting Dog Goes on a Cruise", null,
            [Book("Walter the Farting Dog Goes on a Cruise", "William Kotzwinkle", editions: 1)]));

    [Theory]
    [InlineData("Charles Kittel, Herbert Kroemer")]
    [InlineData("Charles Kittel & Herbert Kroemer")]
    [InlineData("Charles Kittel; Herbert Kroemer")]
    [InlineData("Herbert Kroemer and Charles Kittel")]
    public void Any_of_several_authors_in_one_name_can_match(string authors) =>
        Assert.Equal(MatchDoubts.None, CatalogMatcher.Doubts("Thermal Physics", "Kroemer", Book("Thermal Physics", authors, editions: 10)));

    /// <summary>
    /// Review decisions are stored by lookup key. If this test fails, the key rules changed: stored decisions no longer
    /// match their mentions, and a step must re-key ReviewDecisions from their Title and Author before this ships.
    /// </summary>
    [Theory]
    [InlineData("The Lighthouse Keeper's Almanac", "Ines Calder", "lighthouse keepers almanac|calder")]
    [InlineData("lighthouse keepers almanac", "I. CALDER", "lighthouse keepers almanac|calder")]
    [InlineData("A Walk in the Woods", null, "walk in the woods|")]
    [InlineData("The", null, "the|")]
    [InlineData("Dune Messiah: A Novel", "Frank Herbert", "dune messiah a novel|herbert")]
    [InlineData("The Hobbit", "Tolkien, J.R.R.", "hobbit|tolkien")]
    [InlineData("Strength to Love", "Martin Luther King Jr.", "strength to love|king")]
    [InlineData("1984", "George Orwell", "1984|orwell")]
    [InlineData("  The   New York Times ", "", "new york times|")]
    [InlineData("Économie & Société", "Émile Durkheim", "économie société|durkheim")]
    public void Lookup_keys_stay_the_same(string title, string? author, string expected) =>
        Assert.Equal(expected, CatalogMatcher.LookupKey(title, author));

    [Theory]
    [InlineData(null, "https://openlibrary.org/works/OL1W", true)]
    [InlineData("OpenLibrary", "https://openlibrary.org/works/OL1W", true)]
    [InlineData(null, null, false)]
    [InlineData("OpenLibrary", null, false)]
    [InlineData("SomewhereElse", "https://openlibrary.org/works/OL1W", false)]
    public void A_catalogue_pick_needs_a_known_provider_and_an_https_link(string? provider, string? infoUrl, bool usable) =>
        Assert.Equal(usable, CatalogMatcher.IsUsable(Book("Small Rivers", "Tomas Reyne") with { Provider = provider, InfoUrl = infoUrl }));

    [Theory]
    [InlineData("AppleBooks", "https://books.apple.com/us/book/id900000001", true)]
    [InlineData("Hardcover", "https://hardcover.app/books/harbour-lights", true)]
    [InlineData("Hardcover", "https://hardcover.app.evil.example/books/harbour-lights", false)]
    [InlineData("Hardcover", "https://openlibrary.org/works/OL1W", false)]
    [InlineData("AppleBooks", "https://openlibrary.org/works/OL1W", false)]
    [InlineData("OpenLibrary", "https://books.apple.com/us/book/id900000001", false)]
    [InlineData("OpenLibrary", "https://evil.example/openlibrary.org", false)]
    [InlineData("OpenLibrary", "https://reader@openlibrary.org/works/OL1W", false)]
    [InlineData("OpenLibrary", "https://openlibrary.org:8443/works/OL1W", false)]
    [InlineData("OpenLibrary", "https://openlibrary.org.evil.example/works/OL1W", false)]
    public void A_pick_links_to_its_own_catalogues_host(string provider, string infoUrl, bool usable) =>
        Assert.Equal(usable, CatalogMatcher.IsUsable(Book("Small Rivers", "Tomas Reyne") with { Provider = provider, InfoUrl = infoUrl }));

    [Theory]
    [InlineData(null, "https://covers.openlibrary.org/b/id/12-M.jpg", true)]
    [InlineData("OpenLibrary", "https://attacker.example/x.jpg", false)]
    [InlineData("OpenLibrary", "https://is1-ssl.mzstatic.com/a.jpg", false)]
    [InlineData("AppleBooks", "https://is1-ssl.mzstatic.com/a.jpg", true)]
    [InlineData("AppleBooks", "https://mzstatic.com/a.jpg", true)]
    [InlineData("AppleBooks", "https://evilmzstatic.com/a.jpg", false)]
    [InlineData("AppleBooks", "https://mzstatic.com.evil.example/a.jpg", false)]
    [InlineData("AppleBooks", "http://is1-ssl.mzstatic.com/a.jpg", false)]
    [InlineData("AppleBooks", "https://is1-ssl.mzstatic.com:444/a.jpg", false)]
    public void A_picks_cover_comes_from_its_own_catalogues_image_host(string? provider, string coverUrl, bool usable)
    {
        var infoUrl = provider == "AppleBooks" ? "https://books.apple.com/us/book/id900000001" : "https://openlibrary.org/works/OL1W";
        Assert.Equal(usable, CatalogMatcher.IsUsable(Book("Small Rivers", "Tomas Reyne") with { Provider = provider, InfoUrl = infoUrl, CoverUrl = coverUrl }));
    }

    [Fact]
    public void Without_an_edition_count_a_book_needs_an_author_and_has_no_few_editions_doubt()
    {
        var unknown = Book("Small Rivers", "Tomas Reyne") with { EditionCount = null };

        Assert.Null(CatalogMatcher.Pick("Small Rivers", null, [unknown]));
        Assert.Equal(MatchDoubts.None, CatalogMatcher.Doubts("Small Rivers", "Tomas Reyne", unknown));
    }

    [Theory]
    [InlineData("AppleBooks", true)]
    [InlineData("appleBOOKS", true)]
    [InlineData("Nope", false)]
    [InlineData("Manual", false)]
    [InlineData("OpenLibrary", false)]
    public void Fallbacks_must_be_other_known_catalogues(string fallback, bool valid)
    {
        var options = new BookLookupOptions { Fallbacks = [fallback] };

        Assert.Equal(valid, !options.Validate(new ValidationContext(options)).Any());
    }

    [Fact]
    public void A_fallback_cant_be_listed_twice()
    {
        var options = new BookLookupOptions { Fallbacks = ["AppleBooks", "applebooks"] };

        Assert.NotEmpty(options.Validate(new ValidationContext(options)));
    }

    [Theory]
    [InlineData("0199a5b0c3d47e8fa1b2c3d4e5f60718", null, null, true)]
    [InlineData("OL1W", null, null, false)]
    [InlineData("0199a5b0c3d47e8fa1b2c3d4e5f60718", "https://openlibrary.org/works/OL1W", null, false)]
    [InlineData("0199a5b0c3d47e8fa1b2c3d4e5f60718", "http://openlibrary.org/works/OL1W", null, false)]
    [InlineData("0199a5b0c3d47e8fa1b2c3d4e5f60718", null, "https://covers.test/1.jpg", false)]
    public void A_kept_as_is_pick_has_a_guid_id_and_no_links(string catalogId, string? infoUrl, string? coverUrl, bool usable) =>
        Assert.Equal(usable, CatalogMatcher.IsUsable(
            new CatalogBook(catalogId, "Small Rivers", null, [], null, 0, infoUrl, coverUrl, BookLookupProviders.Manual)));
}
