using System.Diagnostics;
using PoMode.API.Features.Analysis;

namespace PoMode.API.Features.UrlIngest;

/// <summary>
/// Fetches audio from a page URL (YouTube, SoundCloud, …) by shelling out to <c>yt-dlp</c>,
/// which must be on PATH along with <c>ffmpeg</c> for the mp3 extraction. Availability is
/// probed once and cached; when the tool is missing the endpoint reports 503 rather than
/// failing deep in a download.
/// </summary>
public sealed class UrlAudioService(ILogger<UrlAudioService> logger)
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private bool? _available;

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (_available is { } cached)
        {
            return cached;
        }
        await _probeGate.WaitAsync(ct);
        try
        {
            if (_available is { } resolved)
            {
                return resolved;
            }
            _available = await ProbeAsync(ct);
            return _available.Value;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>Downloads the URL's audio as mp3 into a temp folder. Caller deletes the folder.</summary>
    public async Task<(string FileName, string FilePath, string TempDir)> DownloadAsync(string url, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pomode-url-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var outputTemplate = Path.Combine(tempDir, "%(title)s.%(ext)s");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-x");
        startInfo.ArgumentList.Add("--audio-format");
        startInfo.ArgumentList.Add("mp3");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--max-filesize");
        startInfo.ArgumentList.Add("200M"); // pre-transcode cap; the mp3 is checked against MaxBytes below
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputTemplate);
        startInfo.ArgumentList.Add(url);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        _ = process.StandardOutput.ReadToEndAsync(timeout.Token); // drain so the pipe cannot block
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            logger.LogWarning("yt-dlp exited with {Code} for {Url}: {Error}", process.ExitCode, url, stderr);
            throw new InvalidOperationException(FirstLine(stderr) ?? $"yt-dlp exited with code {process.ExitCode}.");
        }

        var files = Directory.GetFiles(tempDir, "*.mp3") is { Length: > 0 } mp3s
            ? mp3s
            : Directory.GetFiles(tempDir);
        if (files.Length == 0)
        {
            throw new InvalidOperationException("The download produced no audio file.");
        }
        var path = files[0];
        if (new FileInfo(path).Length > AudioFormatValidator.MaxBytes)
        {
            throw new InvalidOperationException("The downloaded audio exceeds the 100 MB limit.");
        }
        return (Path.GetFileName(path), path, tempDir);
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "yt-dlp is not available; URL ingest is disabled.");
            return false;
        }
    }

    private static string? FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            ?? text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return string.IsNullOrEmpty(line) ? null : line[..Math.Min(line.Length, 300)];
    }
}
