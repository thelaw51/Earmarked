using Earmarked.Core.Text;

namespace Earmarked.Tests;

public class PlainTextTests
{
    [Fact]
    public void Turns_html_into_paragraphs_of_text_without_scripts()
    {
        const string html = "<p>First &amp; <b>bold</b>.</p><style>p{}</style><p>Second<br/>line</p><script>alert(1)</script>";

        Assert.Equal("First & bold.\n\nSecond\nline", PlainText.FromHtml(html, 100));
    }

    [Fact]
    public void Hostile_markup_is_stripped_quickly()
    {
        var html = string.Concat(Enumerable.Repeat("<script <style ", 200_000)) + "text";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        PlainText.FromHtml(html, 8000);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Took {watch.Elapsed}.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("<p> </p><br>")]
    public void Nothing_but_markup_is_null(string? html) => Assert.Null(PlainText.FromHtml(html, 100));

    [Fact]
    public void Caps_with_an_ellipsis_without_splitting_a_surrogate_pair()
    {
        Assert.Equal("abcd…", PlainText.FromHtml("abcdefgh", 5));
        Assert.Equal("abc…", PlainText.Cap("abc\U0001F600def", 5));
    }
}
