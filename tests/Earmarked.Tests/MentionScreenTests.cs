using Earmarked.Core.Data;
using Earmarked.Core.Extraction;

namespace Earmarked.Tests;

public class MentionScreenTests
{
    private static ExtractedMention Mention(string title, string? author = null, string? evidence = "a book called") =>
        new(title, author, "quote", MentionKind.Passing, 0.9, evidence);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_mention_without_evidence_is_dropped(string? evidence) =>
        Assert.Equal(ScreenReason.NoEvidence, MentionScreen.Check(Mention("Small Rivers", evidence: evidence), "A Show"));

    [Theory]
    [InlineData("The Long Walk", "The Long Walk")]
    [InlineData("long walk!", "The Long Walk")]
    public void The_show_s_own_name_is_dropped(string title, string podcast) =>
        Assert.True(MentionScreen.IsShowName(title, podcast));

    [Theory]
    [InlineData("The Long Walk", "The Long Walk Podcast")]
    [InlineData("Long Walks", "The Long Walk")]
    public void A_title_that_only_resembles_the_show_s_name_stays(string title, string podcast) =>
        Assert.False(MentionScreen.IsShowName(title, podcast));

    [Theory]
    [InlineData("The Long Walk Podcast", true)]
    [InlineData("the harbour lights film", true)]
    [InlineData("A Movie", true)]
    [InlineData("Harbour Lights (film)", true)]
    [InlineData("Harbour Lights (TV series)", true)]
    [InlineData("Harbour Lights (2019 novel)", false)]
    [InlineData("The Film Club", false)]
    [InlineData("Podcasting for Beginners", false)]
    public void A_title_ending_in_another_medium_is_dropped(string title, bool dropped) =>
        Assert.Equal(dropped, MentionScreen.NamesOtherMedium(title));

    [Theory]
    [InlineData("her book")]
    [InlineData("My Book")]
    [InlineData("the book")]
    [InlineData("memoir")]
    [InlineData("his new novel")]
    [InlineData("Mara Quill's book")]
    [InlineData("Mara Quill’s new book")]
    [InlineData("Mara Quill's books")]
    [InlineData("Harbour Lights books")]
    [InlineData("The Mara Quill book")]
    [InlineData("Captain Quill's books on sailing")]
    [InlineData("a great paper")]
    [InlineData("the Harbour Lights series")]
    public void References_that_name_no_book_are_dropped(string title) =>
        Assert.True(MentionScreen.IsReference(title, author: null));

    [Theory]
    [InlineData("The Jungle Book", null)]
    [InlineData("The Book Thief", null)]
    [InlineData("Charlotte's Web", null)]
    [InlineData("The Lighthouse Keeper's Almanac", null)]
    [InlineData("The Books of Jacob", null)]
    [InlineData("A Series of Small Rivers", null)]
    [InlineData("The Captain's Book of Knots", null)]
    [InlineData("The Glass Orchard Book", "Ines Calder")]
    [InlineData("The Harbour Lights Book", "Ines Calder")]
    public void Titles_that_contain_book_words_stay(string title, string? author) =>
        Assert.False(MentionScreen.IsReference(title, author));

    [Theory]
    [InlineData("Ines Calder", "Ines Calder", true)]
    [InlineData("C.S. Quill", "C. S. Quill", true)]
    [InlineData("C.S. Quill", "C.S. Quill", true)]
    [InlineData("Calder", "Ines Calder", true)]
    [InlineData("Small Rivers", "Ines Calder", false)]
    [InlineData("Ines Calder", null, false)]
    public void A_title_that_is_the_author_s_name_is_dropped(string title, string? author, bool dropped) =>
        Assert.Equal(dropped, MentionScreen.IsAuthorName(title, author));

    [Theory]
    [InlineData("The Financial Times", true)]
    [InlineData("the new york times", true)]
    [InlineData("iPad", true)]
    [InlineData("Kindle", true)]
    [InlineData("The Wikipedia page for lighthouses", true)]
    [InlineData("Small Rivers on Goodreads", true)]
    [InlineData("HarbourLights.tv", true)]
    [InlineData("www.harbour-lights.example", true)]
    [InlineData("Dot Com Dreams", false)]
    [InlineData("The Times of My Life", false)]
    [InlineData("Kindling", false)]
    public void Known_non_books_are_dropped(string title, bool dropped) =>
        Assert.Equal(dropped, MentionScreen.IsKnownNonBook(title, null));

    [Fact]
    public void A_named_author_keeps_a_title_that_names_the_show_a_medium_or_a_site()
    {
        Assert.Null(MentionScreen.Check(Mention("A Show", "Mara Quill"), "A Show"));
        Assert.Null(MentionScreen.Check(Mention("How to Watch a Film", "Mara Quill"), "A Show"));
        Assert.Null(MentionScreen.Check(Mention("The Wikipedia Years", "Mara Quill"), "A Show"));
        Assert.Equal(ScreenReason.NotABook, MentionScreen.Check(Mention("The Wall Street Journal", "Mara Quill"), "A Show"));
    }

    [Fact]
    public void A_real_book_passes_every_rule()
    {
        Assert.Null(MentionScreen.Check(Mention("The Lighthouse Keeper's Almanac", "Ines Calder"), "A Show"));
        Assert.Null(MentionScreen.Check(Mention("Small Rivers"), "A Show"));
    }

    [Fact]
    public void Check_reports_the_rule_that_matched()
    {
        Assert.Equal(ScreenReason.ShowName, MentionScreen.Check(Mention("A Show"), "A Show"));
        Assert.Equal(ScreenReason.OtherMedium, MentionScreen.Check(Mention("The Other Show Podcast"), "A Show"));
        Assert.Equal(ScreenReason.Reference, MentionScreen.Check(Mention("her new book"), "A Show"));
        Assert.Equal(ScreenReason.AuthorName, MentionScreen.Check(Mention("Calder", "Ines Calder"), "A Show"));
        Assert.Equal(ScreenReason.NotABook, MentionScreen.Check(Mention("The Wall Street Journal"), "A Show"));
    }
}
