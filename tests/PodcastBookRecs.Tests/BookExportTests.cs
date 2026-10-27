using System.Text;
using System.Text.Json;
using PodcastBookRecs.Core.Books;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Web.Services;

namespace PodcastBookRecs.Tests;

public class BookExportTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    private static readonly Book Emile = new()
    {
        Provider = "Test",
        CatalogId = "OL1W",
        Title = "Émile, or \"On Education\"",
        Authors = "Jean-Jacques Rousseau, Allan Bloom",
        FirstPublishYear = 1762,
        InfoUrl = "https://catalog.test/OL1W",
        CoverUrl = "javascript:alert(1)",
    };

    private static readonly Book Formula = new() { Provider = "Test", CatalogId = "OL2W", Title = "=HYPERLINK(\"x\")", InfoUrl = "http://catalog.test/OL2W" };

    private static readonly BookListRow[] Rows =
    [
        new(1, Emile, new RankedBook(Emile.Id, 2, 3, 1, Now.AddDays(-1))),
        new(4, Formula, new RankedBook(Formula.Id, 1, 1, 0, Now.AddDays(-9))),
    ];

    private static readonly ListedMention[] Mentions =
    [
        new(Emile.Id, Guid.NewGuid(), "Episode 2", "Show B", Now.AddDays(-1), MentionKind.Recommended, TimeSpan.FromSeconds(754.5), "Read it twice."),
        new(Emile.Id, Guid.NewGuid(), "Episode 1", "Show A", Now.AddDays(-5), MentionKind.Discussed, null, "We talked about it."),
        new(Formula.Id, Guid.NewGuid(), "Episode 9", "@Show C", Now.AddDays(-9), MentionKind.Passing, null, "Briefly."),
    ];

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+44", "\"'+44\"")]
    [InlineData("-ish", "\"'-ish\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tcmd", "\"'\tcmd\"")]
    [InlineData("a,b\r\nc", "\"a,b\r\nc\"")]
    [InlineData(null, "\"\"")]
    public void Cells_are_quoted_and_formulas_made_plain_text(string? value, string expected)
    {
        Assert.Equal(expected, BookExport.Cell(value));
    }

    [Fact]
    public void The_plain_csv_has_one_row_per_book_in_list_order()
    {
        var bytes = BookExport.Csv(Rows, Mentions, TimeZoneInfo.Utc);

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n");
        Assert.Equal("\"Rank\",\"Title\",\"Authors\",\"First published\",\"Podcasts\",\"Episodes\",\"Recommended in\",\"Last mentioned\",\"Mentioned on\",\"Catalogue link\"", lines[0]);
        Assert.Equal(
            "\"1\",\"Émile, or \"\"On Education\"\"\",\"Jean-Jacques Rousseau, Allan Bloom\",\"1762\",\"2\",\"3\",\"1\",\"2026-10-26\",\"Show A; Show B\",\"https://catalog.test/OL1W\"",
            lines[1]);
        // The http link isn't https, so it's left out, and the podcast name that looks like a formula is made text.
        Assert.Equal("\"4\",\"'=HYPERLINK(\"\"x\"\")\",\"\",\"\",\"1\",\"1\",\"0\",\"2026-10-18\",\"'@Show C\",\"\"", lines[2]);
        Assert.Equal("", lines[3]);
    }

    [Fact]
    public void The_goodreads_csv_uses_goodreads_column_names_and_the_to_read_shelf()
    {
        var bytes = BookExport.GoodreadsCsv(Rows, Mentions, TimeZoneInfo.Utc, Now);

        Assert.NotEqual(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes).Split("\r\n");
        Assert.Equal(string.Join(',', BookExport.GoodreadsColumns.Select(c => $"\"{c}\"")), lines[0]);
        Assert.Equal(
            "\"Émile, or \"\"On Education\"\"\",\"Jean-Jacques Rousseau\",\"Allan Bloom\",\"\",\"\",\"0\",\"1762\",\"2026/10/27\",\"to-read\",\"to-read\",\"Mentioned on Show A, Show B.\"",
            lines[1]);
    }

    [Fact]
    public void The_json_nests_each_books_mentions_and_records_the_filters()
    {
        var parameters = BookListParameters.Parse(null, "1", "2026-01-01", null, "émile");

        using var json = JsonDocument.Parse(BookExport.Json(Rows, Mentions, parameters, Now));

        var root = json.RootElement;
        Assert.True(root.GetProperty("filter").GetProperty("recommendedOnly").GetBoolean());
        Assert.Equal("2026-01-01", root.GetProperty("filter").GetProperty("from").GetString());
        var book = root.GetProperty("books")[0];
        Assert.Equal(("Émile, or \"On Education\"", 1, 3), (book.GetProperty("title").GetString(), book.GetProperty("rank").GetInt32(), book.GetProperty("episodes").GetInt32()));
        // Only https links survive, as on the pages.
        Assert.Equal(JsonValueKind.Null, book.GetProperty("coverUrl").ValueKind);
        var mention = book.GetProperty("mentions")[0];
        Assert.Equal(("Show B", "Recommended", 754.5), (mention.GetProperty("podcast").GetString(), mention.GetProperty("kind").GetString(), mention.GetProperty("atSeconds").GetDouble()));
        Assert.Equal(2, book.GetProperty("mentions").GetArrayLength());
    }
}
