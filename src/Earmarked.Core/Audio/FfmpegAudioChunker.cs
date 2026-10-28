using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Earmarked.Core.Configuration;

namespace Earmarked.Core.Audio;

/// <summary>A piece of an episode's audio and where it starts in the episode.</summary>
public sealed record AudioChunk(int Index, string Path, TimeSpan Offset);

/// <summary>Splits downloaded audio into chunks the transcription backend can take one at a time.</summary>
public interface IAudioChunker
{
    /// <summary>
    /// Splits <paramref name="sourcePath"/> into chunks in <paramref name="outputDirectory"/>, or returns the chunks from
    /// a previous completed split. Throws <see cref="AudioProcessingException"/> if the audio can't be processed.
    /// </summary>
    Task<IReadOnlyList<AudioChunk>> SplitAsync(string sourcePath, string outputDirectory, TimeSpan chunkLength, CancellationToken cancellationToken);
}

/// <summary>The audio couldn't be read or split. Counts against the episode.</summary>
public sealed class AudioProcessingException(string message) : Exception(message);

/// <summary>
/// Splits audio with ffmpeg into 16 kHz mono WAV chunks. The downloaded file is untrusted, so ffmpeg may only read
/// local files (no playlists that fetch URLs or other files), only in known audio container formats, for at most
/// <see cref="TranscriptionOptions.MaxAudioHours"/> of output, within a wall-clock limit.
/// </summary>
public sealed partial class FfmpegAudioChunker(IOptions<TranscriptionOptions> options, ILogger<FfmpegAudioChunker> logger) : IAudioChunker
{
    /// <summary>Demuxers ffmpeg may use on downloaded audio. Excludes playlist and concat formats, which can open other inputs.</summary>
    internal const string AllowedFormats = "mp3,mov,mp4,m4a,aac,ogg,wav,flac,matroska,webm";

    private const string ListFile = "chunks.csv";
    private const string DoneMarker = "chunks.done";
    private const int MaxStderrChars = 4000;

    /// <summary>Replaces <see cref="TranscriptionOptions.FfmpegTimeoutMinutes"/> in tests, which need a limit shorter than a minute.</summary>
    internal TimeSpan? TimeoutOverride { get; set; }

    public async Task<IReadOnlyList<AudioChunk>> SplitAsync(string sourcePath, string outputDirectory, TimeSpan chunkLength, CancellationToken cancellationToken)
    {
        // ffmpeg runs inside the output folder, so a relative source path would resolve from the wrong place.
        sourcePath = Path.GetFullPath(sourcePath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        var listPath = Path.Combine(outputDirectory, ListFile);
        var donePath = Path.Combine(outputDirectory, DoneMarker);
        if (File.Exists(donePath))
        {
            return ReadList(listPath, outputDirectory);
        }

        foreach (var stale in Directory.EnumerateFiles(outputDirectory, "chunk_*.wav").Append(listPath))
        {
            File.Delete(stale);
        }

        var o = options.Value;
        var start = new ProcessStartInfo(o.FfmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = outputDirectory,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-protocol_whitelist", "file", "-format_whitelist", AllowedFormats,
            "-i", sourcePath,
            "-t", (o.MaxAudioHours * 3600).ToString(CultureInfo.InvariantCulture),
            "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
            "-f", "segment", "-segment_time", ((int)chunkLength.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            "-reset_timestamps", "1", "-segment_list", ListFile, "-segment_list_type", "csv",
            "chunk_%03d.wav",
        })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            lock (stderr)
            {
                if (e.Data is not null && stderr.Length < MaxStderrChars)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        process.OutputDataReceived += (_, _) => { };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AudioProcessingException($"Couldn't start ffmpeg ('{o.FfmpegPath}'): {ex.Message}");
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutOverride ?? TimeSpan.FromMinutes(o.FfmpegTimeoutMinutes));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new AudioProcessingException($"ffmpeg took longer than {o.FfmpegTimeoutMinutes} minutes and was stopped.");
        }

        if (process.ExitCode != 0)
        {
            string detail;
            lock (stderr)
            {
                detail = stderr.ToString().Trim();
            }

            LogFfmpegFailed(process.ExitCode, detail);
            throw new AudioProcessingException($"ffmpeg failed (exit {process.ExitCode}).");
        }

        var chunks = ReadList(listPath, outputDirectory);
        if (chunks.Count == 0)
        {
            throw new AudioProcessingException("ffmpeg found no audio.");
        }

        await File.WriteAllTextAsync(donePath, chunks.Count.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
        return chunks;
    }

    /// <summary>Reads ffmpeg's segment list: one <c>file,start,end</c> line per chunk, times in seconds.</summary>
    private static List<AudioChunk> ReadList(string listPath, string directory) =>
        File.ReadAllLines(listPath)
            .Where(l => l.Length > 0)
            .Select((line, i) =>
            {
                var parts = line.Split(',');
                return new AudioChunk(i, Path.Combine(directory, parts[0]), TimeSpan.FromSeconds(double.Parse(parts[1], CultureInfo.InvariantCulture)));
            })
            .ToList();

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffmpeg exited with {ExitCode}: {Detail}")]
    private partial void LogFfmpegFailed(int exitCode, string detail);
}
