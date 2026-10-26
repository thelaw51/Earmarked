namespace PodcastBookRecs.Core.Data;

/// <summary>
/// A book mentioned in an episode, as found by the extractor and checked against the transcript. Belongs to the
/// transcript it came from, so replacing or deleting the transcript removes its mentions.
/// </summary>
public class BookMention
{
    public const int MaxTitleLength = 300;
    public const int MaxAuthorLength = 200;
    public const int MaxQuoteLength = 1000;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid EpisodeId { get; set; }
    public Episode Episode { get; set; } = null!;
    public Guid TranscriptId { get; set; }
    public Transcript Transcript { get; set; } = null!;

    /// <summary>The title as the model gave it. Open Library resolution (Phase 3, PR B) adds the canonical book.</summary>
    public required string Title { get; set; }
    public string? Author { get; set; }

    /// <summary>The transcript passage the mention was found in, as the transcript has it.</summary>
    public required string Quote { get; set; }
    public MentionKind Kind { get; set; }

    /// <summary>The model's own 0 to 1 confidence that this is a real, identifiable book.</summary>
    public double Confidence { get; set; }

    /// <summary>Where the quote starts in the episode. Null when the transcript has no timings.</summary>
    public TimeSpan? StartTime { get; set; }

    public required string Provider { get; set; }
    public required string Model { get; set; }

    /// <summary>Which prompt produced this mention, so mentions from an older prompt can be found and re-run.</summary>
    public required string PromptVersion { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>How a book came up. Values are stored, so never renumber them.</summary>
public enum MentionKind
{
    Passing = 0,
    Discussed = 1,
    Recommended = 2,
    OwnBook = 3,
    Criticised = 4,
}
