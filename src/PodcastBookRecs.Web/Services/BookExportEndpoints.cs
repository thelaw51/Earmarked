using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PodcastBookRecs.Core.Books;
using PodcastBookRecs.Core.Data;

namespace PodcastBookRecs.Web.Services;

/// <summary>
/// The Books page's downloads. Each takes the page's own query string, so a file holds exactly the filtered list on
/// screen. They sit behind the fallback sign-in policy like every other endpoint.
/// </summary>
public static class BookExportEndpoints
{
    public static IEndpointRouteBuilder MapBookExports(this IEndpointRouteBuilder app)
    {
        app.MapGet("/books/export.csv", (HttpContext context, IDbContextFactory<AppDbContext> dbFactory, SchemaStatus schema, TimeProvider time) =>
            ExportAsync(context, dbFactory, schema, time, Format.Csv));
        app.MapGet("/books/export-goodreads.csv", (HttpContext context, IDbContextFactory<AppDbContext> dbFactory, SchemaStatus schema, TimeProvider time) =>
            ExportAsync(context, dbFactory, schema, time, Format.Goodreads));
        app.MapGet("/books/export.json", (HttpContext context, IDbContextFactory<AppDbContext> dbFactory, SchemaStatus schema, TimeProvider time) =>
            ExportAsync(context, dbFactory, schema, time, Format.Json));
        return app;
    }

    internal enum Format
    {
        Csv,
        Goodreads,
        Json,
    }

    internal static async Task<IResult> ExportAsync(
        HttpContext context, IDbContextFactory<AppDbContext> dbFactory, SchemaStatus schema, TimeProvider time, Format format)
    {
        var cancellationToken = context.RequestAborted;
        // Until the Worker has created the database, touching it would create an empty file.
        if (!await schema.IsReadyAsync(cancellationToken))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var query = context.Request.Query;
        var parameters = BookListParameters.Parse(query["podcast"], query["rec"], query["from"], query["to"], query["q"]);
        var filter = parameters.ToFilter(TimeZoneInfo.Local);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await BookListQuery.LoadAsync(db, filter, cancellationToken);
        var mentions = await BookListQuery.LoadMentionsAsync(db, filter, rows, cancellationToken);

        var now = time.GetUtcNow().UtcDateTime;
        var stamp = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        // The list is the person's own data: keep it out of shared caches.
        context.Response.Headers.CacheControl = "no-store";
        return format switch
        {
            Format.Csv => Results.File(BookExport.Csv(rows, mentions, TimeZoneInfo.Local), "text/csv; charset=utf-8", $"podcast-books-{stamp}.csv"),
            Format.Goodreads => Results.File(
                BookExport.GoodreadsCsv(rows, mentions, TimeZoneInfo.Local, now), "text/csv; charset=utf-8", $"podcast-books-goodreads-{stamp}.csv"),
            _ => Results.File(BookExport.Json(rows, mentions, parameters, now), "application/json", $"podcast-books-{stamp}.json"),
        };
    }
}
