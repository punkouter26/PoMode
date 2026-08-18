using System.Text.Json;
using PoMode.API.Features.Audio;
using PoMode.API.Features.Cloud;

namespace PoMode.API.Features.StemSeparation;

/// <summary>
/// Shared plumbing for the cloud stem separators: downloading a provider's stem into a real wav,
/// and parsing a provider response body into a <see cref="JsonElement"/> that outlives its document.
/// </summary>
public static class StemDownloader
{
    /// <summary>
    /// Downloads the stem, then re-encodes it through <see cref="WavWriter"/>. The provider may hand
    /// back any container it likes; every downstream stage expects the destination to really be a wav.
    /// </summary>
    public static async Task DownloadStemAsync(
        HttpClient client,
        string url,
        string destination,
        string providerName,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct)
    {
        using var response = await ResilientHttp.SendAsync(
            client, () => new HttpRequestMessage(HttpMethod.Get, url), time, logger, ct,
            HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not download a separated stem from {providerName} ({(int)response.StatusCode}).");
        }

        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $"{Guid.NewGuid():N}.download");
        try
        {
            // Stream straight to disk: a stem is ~40MB and two downloads can run concurrently,
            // so buffering the whole body would put both copies on the large-object heap at once.
            await using (var body = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(temporary))
            {
                await body.CopyToAsync(file, ct);
            }
            WavWriter.Write(destination, AudioDecoder.Decode(temporary));
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone(); // the document is disposed; Clone survives it
    }
}
