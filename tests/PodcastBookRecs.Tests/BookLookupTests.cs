using System.ComponentModel.DataAnnotations;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Providers.BookLookup.OpenLibrary;

namespace PodcastBookRecs.Tests;

public class BookLookupTests
{
    private static CatalogBook Book(string title, string author, int editions = 2, string? subtitle = null, string id = "OL1W") =>
        new(id, title, subtitle, [author], null, editions, $"https://catalog.test/{id}", null);

    [Theory]
    [InlineData("the lighthouse keepers almanac", null)]
    [InlineData("Lighthouse Keeper's Almanac", "Ines Calder")]
    [InlineData("The Lighthouse Keeper's Almanac: A Novel", "I. Calder")]
    public void Picks_the_same_title_ignoring_case_punctuation_articles_and_subtitles(string title, string? author) =>
        Assert.Equal("OL1W", CatalogMatcher.Pick(title, author, [Book("The Lighthouse Keeper's Almanac", "Ines Calder")])?.CatalogId);

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
}
