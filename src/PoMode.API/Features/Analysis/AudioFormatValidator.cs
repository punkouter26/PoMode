namespace PoMode.API.Features.Analysis;

/// <summary>Why an upload was rejected; each endpoint renders its own message text.</summary>
public enum UploadRejection
{
    TooLarge,
    UnsupportedFormat,
}

/// <summary>Magic-byte sniffing for uploads; extension is never trusted.</summary>
public static class AudioFormatValidator
{
    public const long MaxBytes = 100L * 1024 * 1024;

    /// <summary>The hum take's multipart file, through the same rule as a resumable upload.</summary>
    public static async Task<UploadRejection?> ValidateAsync(IFormFile file, CancellationToken ct)
    {
        await using var probe = file.OpenReadStream();
        return await ValidateAsync(probe, file.Length, ct);
    }

    /// <summary>The one upload rule: size cap plus a 12-byte header sniff, read from the stream's
    /// current position. Null when the file is acceptable. The stream is left part-read, so a caller
    /// that goes on to store the file opens a fresh one.</summary>
    public static async Task<UploadRejection?> ValidateAsync(Stream content, long length, CancellationToken ct)
    {
        if (length > MaxBytes)
        {
            return UploadRejection.TooLarge;
        }
        var header = new byte[12];
        var read = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        return IsSupported(header.AsSpan(0, read), out _) ? null : UploadRejection.UnsupportedFormat;
    }

    public static bool IsSupported(ReadOnlySpan<byte> header, out string format)
    {
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
        {
            format = "wav";
            return true;
        }

        if (header.Length >= 3 && header[..3].SequenceEqual("ID3"u8))
        {
            format = "mp3";
            return true;
        }

        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
        {
            format = "mp3";
            return true;
        }

        format = "";
        return false;
    }
}
