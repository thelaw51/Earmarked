using Earmarked.Web.Services;

namespace Earmarked.Tests;

public class ShowNotesTests
{
    [Theory]
    [InlineData("Books: The Lighthouse Keeper’s Almanac by Ines Calder.", "The Lighthouse Keeper's Almanac", "Ines Calder", true)]
    [InlineData("THE LIGHTHOUSE KEEPERS ALMANAC (2019)", "The Lighthouse Keeper's Almanac: A Year on the Rock", "", true)]
    [InlineData("Lighthouse keepers and their almanacs", "The Lighthouse Keeper's Almanac", "Ines Calder", false)]
    [InlineData("We read Dune by Frank Herbert.", "Dune", "Frank Herbert", true)]
    [InlineData("We walked over a dune.", "Dune", "Frank Herbert", false)]
    [InlineData("It was a good week.", "It", "Stephen King", false)]
    [InlineData(null, "Dune", "Frank Herbert", false)]
    public void Finds_a_title_as_whole_words_and_a_one_word_title_only_with_its_author(string? notes, string title, string authors, bool found) =>
        Assert.Equal(found, ShowNotes.MentionsBook(notes, title, authors));
}
