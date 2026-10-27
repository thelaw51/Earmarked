using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;
using PodcastBookRecs.Core.Data;
using PodcastBookRecs.Web.Services;

namespace PodcastBookRecs.Tests;

/// <summary>The export endpoints' handler, run against a request with no server around it.</summary>
public sealed class BookExportEndpointTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("pbr-export-").FullName;
    private readonly PathsOptions _paths = new();
    private IDbContextFactory<AppDbContext> _dbFactory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _paths.Data = _root;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_paths.DatabaseFile}")
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        _dbFactory = new PooledDbContextFactory<AppDbContext>(options);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Before_the_worker_makes_the_database_an_export_is_unavailable_and_creates_no_file()
    {
        var context = await ExportAsync(BookExportEndpoints.Format.Csv);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(File.Exists(_paths.DatabaseFile));
    }

    [Theory]
    [InlineData("Csv", "text/csv; charset=utf-8", "podcast-books-")]
    [InlineData("Goodreads", "text/csv; charset=utf-8", "podcast-books-goodreads-")]
    [InlineData("Json", "application/json", "podcast-books-")]
    public async Task An_export_is_a_download_that_no_cache_keeps(string format, string type, string filename)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(Ct))
        {
            await db.Database.MigrateAsync(Ct);
        }

        // The format arrives as text because the enum is internal and a public test method can't take it.
        var context = await ExportAsync(Enum.Parse<BookExportEndpoints.Format>(format));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(type, context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Contains($"attachment; filename={filename}", context.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
    }

    private async Task<HttpContext> ExportAsync(BookExportEndpoints.Format format)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            RequestAborted = Ct,
        };
        context.Response.Body = new MemoryStream();
        var schema = new SchemaStatus(_dbFactory, Options.Create(_paths));

        var result = await BookExportEndpoints.ExportAsync(context, _dbFactory, schema, TimeProvider.System, format);
        await result.ExecuteAsync(context);
        return context;
    }
}
