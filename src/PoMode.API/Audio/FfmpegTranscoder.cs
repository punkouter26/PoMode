using System.ComponentModel;
using System.Diagnostics;
using PoMode.API.Features.Analysis;

namespace PoMode.API.Audio;

/// <summary>
/// Converts an uploaded MP4/M4A to WAV with the host's <c>ffmpeg</c>, for the browser that could not
/// do it itself.
/// </summary>
/// <remarks>
/// The ordinary path never comes here: <c>resumable-upload.js</c> decodes an .m4a in the browser and
/// uploads WAV. This is the fallback for a browser with no AAC decoder (an editor's built-in browser,
/// some Linux Chromium builds), and it exists only where an <c>ffmpeg</c> is on the PATH. Nothing
/// installs one: a developer machine usually has it, the Azure host does not, and there the upload is
/// refused in words rather than half-working. A managed decoder was tried first and cannot read
/// HE-AAC, which is what Android's recorder writes.
/// </remarks>
public static class FfmpegTranscoder
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Writes <paramref name="source"/>'s audio as 16-bit PCM WAV to <paramref name="wavPath"/>.
    /// False when there is no <c>ffmpeg</c>, it refused the file, or it ran out of time.
    /// </summary>
    public static async Task<bool> TryToWavAsync(string source, string wavPath, ILogger logger, CancellationToken ct)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // -fs stops the output a byte past the upload cap, so an hour of AAC cannot fill the disk;
        // the caller then refuses it as too large like any other oversized file.
        foreach (var argument in (string[])
            ["-v", "error", "-nostdin", "-y", "-i", source, "-vn", "-c:a", "pcm_s16le",
             "-fs", (AudioFormatValidator.MaxBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
             "-f", "wav", wavPath])
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            try
            {
                var errors = await process.StandardError.ReadToEndAsync(limit.Token);
                await process.WaitForExitAsync(limit.Token);
                if (process.ExitCode != 0)
                {
                    logger.LogWarning("ffmpeg refused an upload: {Errors}", errors.Trim());
                    return false;
                }
                return File.Exists(wavPath);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
        }
        catch (Win32Exception)
        {
            // No ffmpeg on this host.
            return false;
        }
    }
}
