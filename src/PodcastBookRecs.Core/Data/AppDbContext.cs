using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PodcastBookRecs.Core.Transcripts;

namespace PodcastBookRecs.Core.Data;

/// <summary>EF Core context shared by the Worker and the UI. Only the Worker applies migrations.</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<Podcast> Podcasts => Set<Podcast>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Podcast>(b =>
        {
            b.HasIndex(p => p.FeedUrl).IsUnique();
            b.HasMany(p => p.Episodes).WithOne(e => e.Podcast).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Episode>(b =>
        {
            b.HasIndex(e => new { e.PodcastId, e.IdentityKey }).IsUnique();
            b.HasIndex(e => new { e.State, e.NextAttemptAt });
            b.Property(e => e.TranscriptLinks).HasConversion(JsonConverter<List<TranscriptLink>>(), JsonComparer<List<TranscriptLink>>());
            b.HasOne(e => e.Transcript).WithOne(t => t.Episode).HasForeignKey<Transcript>(t => t.EpisodeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Transcript>(b =>
        {
            b.HasIndex(t => t.EpisodeId).IsUnique();
            b.Property(t => t.Segments).HasConversion(JsonConverter<List<TranscriptSegment>>(), JsonComparer<List<TranscriptSegment>>());
        });

        modelBuilder.Entity<WorkerCommand>(b => b.HasIndex(c => c.CompletedAt));

        modelBuilder.Entity<WorkerStatus>(b => b.HasKey(s => s.Name));
    }

    private static ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, JsonOptions), v => JsonSerializer.Deserialize<T>(v, JsonOptions) ?? new T());

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
