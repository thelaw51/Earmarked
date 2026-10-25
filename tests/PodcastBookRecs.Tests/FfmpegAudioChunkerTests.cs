using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PodcastBookRecs.Core.Audio;
using PodcastBookRecs.Core.Configuration;

namespace PodcastBookRecs.Tests;

/// <summary>
/// Runs the real ffmpeg. Skipped locally when ffmpeg isn't installed, but fails on CI (where <c>CI=true</c>), because
/// the Docker image ships ffmpeg and a silently skipped test would leave it untested.
/// </summary>
public sealed class FfmpegAudioChunkerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pbr-ffmpeg-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Splits_audio_with_cover_art_into_chunks_with_real_offsets()
    {
        RequireFfmpeg();
        var source = await MakeMp3WithCoverArtAsync(seconds: 50);

        var chunks = await Chunker().SplitAsync(source, _dir, TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(TimeSpan.Zero, chunks[0].Offset);
        Assert.InRange(chunks[1].Offset.TotalSeconds, 19.9, 20.1);
        Assert.InRange(chunks[2].Offset.TotalSeconds, 39.9, 40.1);
        Assert.All(chunks, c => Assert.True(File.Exists(c.Path)));

        // Each chunk is its own WAV starting at zero, so the transcriber adds the offset exactly once.
        Assert.InRange(await DurationAsync(chunks[1].Path), 19.9, 20.1);
        Assert.InRange(await DurationAsync(chunks[2].Path), 9.8, 10.1);
    }

    [Fact]
    public async Task Accepts_paths_relative_to_the_current_directory()
    {
        // Regression: ffmpeg runs inside the output folder, so relative paths used to resolve from the wrong place.
        RequireFfmpeg();
        var source = await MakeMp3WithCoverArtAsync(seconds: 5);

        var chunks = await Chunker().SplitAsync(
            Path.GetRelativePath(Environment.CurrentDirectory, source), Path.GetRelativePath(Environment.CurrentDirectory, _dir), TimeSpan.FromSeconds(20), Ct);

        Assert.True(File.Exists(Assert.Single(chunks).Path));
    }

    [Fact]
    public async Task Reuses_a_completed_split()
    {
        RequireFfmpeg();
        var source = await MakeMp3WithCoverArtAsync(seconds: 25);
        var first = await Chunker().SplitAsync(source, _dir, TimeSpan.FromSeconds(20), Ct);

        File.Delete(source);
        var second = await Chunker().SplitAsync(source, _dir, TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("ffconcat version 1.0\nfile '/etc/hosts'\n")]
    [InlineData("#EXTM3U\n#EXTINF:10,\nhttp://127.0.0.1:9/x.mp3\n")]
    public async Task Refuses_playlists_that_would_make_ffmpeg_open_other_inputs(string content)
    {
        RequireFfmpeg();
        var source = Path.Combine(_dir, "source.audio");
        await File.WriteAllTextAsync(source, content, Ct);

        var ex = await Assert.ThrowsAsync<AudioProcessingException>(() => Chunker().SplitAsync(source, _dir, TimeSpan.FromSeconds(20), Ct));

        Assert.StartsWith("ffmpeg failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stops_ffmpeg_that_runs_past_its_time_limit()
    {
        RequireFfmpeg();
        var source = await MakeMp3WithCoverArtAsync(seconds: 600);
        var chunker = Chunker();
        chunker.TimeoutOverride = TimeSpan.FromMilliseconds(1);

        var ex = await Assert.ThrowsAsync<AudioProcessingException>(() => chunker.SplitAsync(source, _dir, TimeSpan.FromSeconds(20), Ct));

        Assert.Contains("was stopped", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_missing_ffmpeg_binary()
    {
        var chunker = new FfmpegAudioChunker(
            Options.Create(new TranscriptionOptions { FfmpegPath = "/nonexistent/ffmpeg" }), NullLogger<FfmpegAudioChunker>.Instance);

        var ex = await Assert.ThrowsAsync<AudioProcessingException>(() => chunker.SplitAsync(Path.Combine(_dir, "x"), _dir, TimeSpan.FromSeconds(20), Ct));

        Assert.StartsWith("Couldn't start ffmpeg", ex.Message, StringComparison.Ordinal);
    }

    private static FfmpegAudioChunker Chunker() =>
        new(Options.Create(new TranscriptionOptions()), NullLogger<FfmpegAudioChunker>.Instance);

    /// <summary>A tone with an embedded cover picture, like real podcast MP3s whose ID3 artwork shows up as a video stream.</summary>
    private async Task<string> MakeMp3WithCoverArtAsync(int seconds)
    {
        var cover = Path.Combine(_dir, "cover.png");
        var mp3 = Path.Combine(_dir, "source.audio");
        await RunAsync("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=64x64", "-frames:v", "1", cover);
        await RunAsync("ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}", "-i", cover,
            "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame", "-b:a", "32k", "-c:v", "mjpeg", "-disposition:v", "attached_pic",
            "-id3v2_version", "3", "-f", "mp3", mp3);
        File.Delete(cover);
        return mp3;
    }

    private static async Task<double> DurationAsync(string path) =>
        double.Parse(await RunAsync("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path), CultureInfo.InvariantCulture);

    private static async Task<string> RunAsync(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"{file} failed: {error}");
    }

    private static void RequireFfmpeg()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, UseShellExecute = false })!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
            {
                Assert.Fail("ffmpeg must be installed on CI: the Docker image depends on it.");
            }

            Assert.Skip("ffmpeg isn't installed.");
        }
    }
}
