using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Configuration;

namespace PodcastBookRecs.Core.Pipeline;

/// <summary>Per-episode scratch folders under <c>/data/work</c>: the downloaded audio, its chunks, and each finished chunk's segments.</summary>
public sealed class TranscriptionWorkspace(IOptions<PathsOptions> paths)
{
    public string Root => Path.GetFullPath(Path.Combine(paths.Value.Data, "work"));

    public string For(Guid episodeId) => Path.Combine(Root, episodeId.ToString("N"));

    public void Delete(Guid episodeId)
    {
        var dir = For(episodeId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Episode IDs that currently have a folder. Folders whose names aren't episode IDs are ignored.</summary>
    public IEnumerable<Guid> Existing() =>
        Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).Select(Path.GetFileName).Select(n => Guid.TryParseExact(n, "N", out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)
            : [];

    /// <summary>Free bytes on the volume holding the data folder.</summary>
    public long FreeBytes()
    {
        Directory.CreateDirectory(Root);
        return new DriveInfo(Root).AvailableFreeSpace;
    }
}
