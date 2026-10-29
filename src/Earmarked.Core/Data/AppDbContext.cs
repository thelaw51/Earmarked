using System.Text.Json;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Earmarked.Core.BookLookup;
using Earmarked.Core.Transcripts;

namespace Earmarked.Core.Data;

/// <summary>
/// EF Core context shared by the Worker and the UI. Only the Worker applies migrations.
/// Entity keys are version 7 GUIDs, created in code when an entity is constructed: they start with a timestamp, so
/// new rows append to the end of each index instead of landing at random positions, and an ID exists before saving.
/// The Identity base adds users, claims, logins and tokens, but no roles: the app has one kind of user.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityUserContext<AppUser, Guid>(options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<PodcastFeed> PodcastFeeds => Set<PodcastFeed>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
    public DbSet<BookMention> BookMentions => Set<BookMention>();
    public DbSet<Book> Books => Set<Book>();
    public DbSet<ReviewDecision> ReviewDecisions => Set<ReviewDecision>();
    public DbSet<CatalogSearch> CatalogSearches => Set<CatalogSearch>();
    public DbSet<WorkerCommand> WorkerCommands => Set<WorkerCommand>();
    public DbSet<WorkerStatus> WorkerStatuses => Set<WorkerStatus>();

    /// <summary>
    /// SQLite has no date type, so DateTimes come back with Kind Unspecified. Every DateTime in this model
    /// is UTC, so mark them as such on read.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PodcastFeed>(b =>
        {
            b.HasIndex(p => p.FeedUrl).IsUnique();
            b.HasMany(p => p.Episodes).WithOne(e => e.PodcastFeed).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Episode>(b =>
        {
            b.HasIndex(e => new { e.PodcastFeedId, e.IdentityKey }).IsUnique();
            b.HasIndex(e => new { e.State, e.NextAttemptAt });
            b.Property(e => e.TranscriptLinks).HasConversion(JsonConverter<List<TranscriptLink>>(), JsonComparer<List<TranscriptLink>>());
            b.Property(e => e.Description).HasMaxLength(Episode.MaxDescriptionLength);
            b.HasOne(e => e.Transcript).WithOne(t => t.Episode).HasForeignKey<Transcript>(t => t.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Transcript>(b =>
        {
            b.HasIndex(t => t.EpisodeId).IsUnique();
            b.Property(t => t.Segments).HasConversion(JsonConverter<List<TranscriptSegment>>(), JsonComparer<List<TranscriptSegment>>());
        });

        // Mentions belong to the transcript they were extracted from: replacing or deleting it removes them.
        builder.Entity<BookMention>(b =>
        {
            b.HasOne(m => m.Episode).WithMany(e => e.Mentions).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(m => m.Transcript).WithMany(t => t.Mentions).OnDelete(DeleteBehavior.Cascade);
            b.Property(m => m.Title).HasMaxLength(BookMention.MaxTitleLength);
            b.Property(m => m.Author).HasMaxLength(BookMention.MaxAuthorLength);
            b.Property(m => m.Quote).HasMaxLength(BookMention.MaxQuoteLength);
            b.Property(m => m.LookupKey).HasMaxLength(BookMention.MaxTitleLength + BookMention.MaxAuthorLength);
            b.Property(m => m.MatcherVersion).HasMaxLength(40);
            b.HasOne(m => m.Book).WithMany(k => k.Mentions).OnDelete(DeleteBehavior.SetNull);
            b.HasIndex(m => new { m.LookupKey, m.MatcherVersion });
            b.HasIndex(m => m.MatcherVersion);
        });

        builder.Entity<Book>(b =>
        {
            b.HasIndex(k => new { k.Provider, k.CatalogId }).IsUnique();
            b.Property(k => k.Provider).HasMaxLength(40);
            b.Property(k => k.CatalogId).HasMaxLength(40);
            b.Property(k => k.Title).HasMaxLength(Book.MaxTitleLength);
            b.Property(k => k.Authors).HasMaxLength(Book.MaxAuthorsLength);
            b.Property(k => k.InfoUrl).HasMaxLength(Book.MaxUrlLength);
            b.Property(k => k.CoverUrl).HasMaxLength(Book.MaxUrlLength);
            b.Property(k => k.Description).HasMaxLength(Book.MaxDescriptionLength);
            b.Property(k => k.Subjects).HasConversion(JsonConverter<List<string>>(), JsonComparer<List<string>>());
            b.HasIndex(k => k.DetailsCheckedAt);
        });

        builder.Entity<ReviewDecision>(b =>
        {
            b.HasIndex(d => d.LookupKey).IsUnique();
            b.Property(d => d.LookupKey).HasMaxLength(BookMention.MaxTitleLength + BookMention.MaxAuthorLength);
            b.Property(d => d.Title).HasMaxLength(BookMention.MaxTitleLength);
            b.Property(d => d.Author).HasMaxLength(BookMention.MaxAuthorLength);
            b.Property(d => d.Note).HasMaxLength(ReviewDecision.MaxNoteLength);
            b.Property(d => d.Pick).HasConversion(NullableJsonConverter<CatalogBook>(), NullableJsonComparer<CatalogBook>());
            b.HasOne(d => d.Book).WithMany().OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CatalogSearch>(b =>
        {
            b.HasIndex(c => c.CompletedAt);
            b.Property(c => c.Title).HasMaxLength(BookMention.MaxTitleLength);
            b.Property(c => c.Author).HasMaxLength(BookMention.MaxAuthorLength);
            b.Property(c => c.Error).HasMaxLength(CatalogSearch.MaxErrorLength);
            b.Property(c => c.Results).HasConversion(NullableJsonConverter<List<CatalogBook>>(), NullableJsonComparer<List<CatalogBook>>());
        });

        builder.Entity<WorkerCommand>(b =>
        {
            b.HasIndex(c => c.CompletedAt);
            b.Property(c => c.LookupKey).HasMaxLength(BookMention.MaxTitleLength + BookMention.MaxAuthorLength);
        });

        builder.Entity<WorkerStatus>(b => b.HasKey(s => s.Name));
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, JsonOptions), v => JsonSerializer.Deserialize<T>(v, JsonOptions) ?? new T());

    /// <summary>For a column that may be empty. A value that no longer parses reads as null rather than failing the whole query.</summary>
    private static ValueConverter<T?, string?> NullableJsonConverter<T>() where T : class =>
        new(v => v == null ? null : JsonSerializer.Serialize(v, JsonOptions), v => v == null ? null : DeserializeOrNull<T>(v));

    private static T? DeserializeOrNull<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ValueComparer<T?> NullableJsonComparer<T>() where T : class =>
        new(
            (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
            v => v == null ? 0 : JsonSerializer.Serialize(v, JsonOptions).GetHashCode(StringComparison.Ordinal),
            v => v == null ? null : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions));

    private static ValueComparer<T> JsonComparer<T>() =>
        new(
            (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
            v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!);

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? v.Value.ToUniversalTime() : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
