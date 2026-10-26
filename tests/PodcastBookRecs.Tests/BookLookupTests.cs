using System.ComponentModel.DataAnnotations;
using PodcastBookRecs.Core.BookLookup;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Providers.BookLookup.OpenLibrary;

namespace PodcastBookRecs.Tests;

public class BookLookupTests
{
    private static CatalogBook Book(string title, string author, int editions = 1, string? subtitle = null, string id = "OL1W") =>
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
