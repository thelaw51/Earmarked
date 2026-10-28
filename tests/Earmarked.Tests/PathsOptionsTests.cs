using Earmarked.Core.Configuration;

namespace Earmarked.Tests;

public sealed class PathsOptionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("earmarked-paths-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_new_install_uses_the_new_file_name() =>
        Assert.Equal(Path.Combine(_root, "earmarked.db"), new PathsOptions { Data = _root }.DatabaseFile);

    [Fact]
    public void An_install_from_before_the_rename_keeps_its_database()
    {
        File.WriteAllText(Path.Combine(_root, "podcastbookrecs.db"), "");

        Assert.Equal(Path.Combine(_root, "podcastbookrecs.db"), new PathsOptions { Data = _root }.DatabaseFile);
    }

    [Fact]
    public void The_new_file_wins_once_it_exists()
    {
        File.WriteAllText(Path.Combine(_root, "podcastbookrecs.db"), "");
        File.WriteAllText(Path.Combine(_root, "earmarked.db"), "");

        Assert.Equal(Path.Combine(_root, "earmarked.db"), new PathsOptions { Data = _root }.DatabaseFile);
    }
}
