using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace PoMode.E2EAPI;

/// <summary>Factory whose clients present the FakeAuth headers, matching the Blazor client's
/// defaults — the write endpoints (cancel, client-result, from-url) require them.</summary>
public sealed class AuthedFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Every test in this assembly shares one in-process server and one partition key, and they
        // drive the endpoints at machine speed — so the per-minute limits would be measuring the
        // test runner rather than the app. Switched off rather than raised, because a limit set high
        // enough for a test suite protects nothing in production.
        builder.UseSetting("RateLimits:Enabled", "false");
        // No first-run demo: its template build would hold the single worker the tests' own jobs
        // queue on, and a seeded row would sit in libraries they assert on. DemoSongTests turns it on.
        builder.UseSetting("Demo:Enabled", "false");
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-Fake-User", "e2e");
        client.DefaultRequestHeaders.Add("X-Fake-Roles", "listener");
    }
}

public static class UploadClientExtensions
{
    public const string TusVersion = "1.0.0";

    /// <summary>
    /// Uploads audio the one way the app takes it: a tus upload (created and filled in a single
    /// request, the creation-with-upload extension), then the call that hands it to the analyzer.
    /// Returns that call's response — a <c>JobStatusDto</c> on success, a reason on refusal.
    /// </summary>
    public static async Task<HttpResponseMessage> UploadAudioAsync(
        this HttpClient client, byte[] audio, string fileName = "test.wav", string query = "")
    {
        var uploadId = await CreateUploadAsync(client, audio.Length, fileName, audio);
        return await client.PostAsync($"/api/analysis/uploads/{uploadId}{query}", content: null);
    }

    /// <summary>Creates a tus upload of <paramref name="length"/> bytes, optionally sending
    /// <paramref name="firstBytes"/> with it, and returns its id.</summary>
    public static async Task<string> CreateUploadAsync(
        HttpClient client, long length, string fileName, byte[]? firstBytes = null)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/uploads");
        create.Headers.Add("Tus-Resumable", TusVersion);
        create.Headers.Add("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
        create.Headers.Add("Upload-Metadata", "filename " + Convert.ToBase64String(Encoding.UTF8.GetBytes(fileName)));
        if (firstBytes is not null)
        {
            create.Content = new ByteArrayContent(firstBytes);
            create.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        }
        using var created = await client.SendAsync(create);
        created.EnsureSuccessStatusCode();
        return created.Headers.Location!.OriginalString.Split('/')[^1];
    }
}
