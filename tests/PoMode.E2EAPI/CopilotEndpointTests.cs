using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class CopilotEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-copilot-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-copilot-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    /// <summary>
    /// Points the copilot at a loopback port with nothing listening, so these tests exercise the
    /// "no Ollama installed" path deterministically whether or not the dev box happens to run one.
    /// </summary>
    private WebApplicationFactory<Program> Factory() => new AuthedFactory()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false")
            .UseSetting("Copilot:BaseUrl", "http://127.0.0.1:5399"));

    /// <summary>Four triads, so the job has real chords and therefore real modal windows.</summary>
    private static byte[] Progression()
    {
        using var stream = new MemoryStream();
        foreach (var (root, quality) in new[] { (0, "maj"), (9, "min"), (5, "maj"), (7, "maj") })
        {
            var wav = TestAudio.MakeChord(2.0, TestAudio.Triad(root, quality));
            var skipHeader = stream.Length == 0 ? 0 : 44;
            stream.Write(wav, skipHeader, wav.Length - skipHeader);
        }
        var bytes = stream.ToArray();
        var dataSize = bytes.Length - 44;
        BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
        return bytes;
    }

    private static async Task<string> CompletedJobAsync(HttpClient client)
    {
        var content = new ByteArrayContent(Progression());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent { { content, "file", "test.wav" } };

        var created = await (await client.PostAsync("/api/analysis", form)).Content.ReadFromJsonAsync<JobStatusDto>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{created!.JobId}");
            if (status!.Stage is JobStage.Complete) return created.JobId;
            if (status.Stage is JobStage.Failed or JobStage.Cancelled)
            {
                throw new InvalidOperationException($"Job ended as {status.Stage}: {status.Error}");
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("Job did not complete in 15s.");
    }

    [Fact]
    public async Task With_no_ollama_it_returns_an_unavailable_reply_not_a_server_error()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        var response = await client.PostAsJsonAsync("/api/copilot/explain", new CopilotRequest(jobId, 0));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // NOT a 500
        var reply = await response.Content.ReadFromJsonAsync<CopilotReply>();
        Assert.NotNull(reply);
        Assert.False(reply.Available);
        Assert.Null(reply.Markdown);
        Assert.False(string.IsNullOrWhiteSpace(reply.Reason), "an unavailable copilot must say why");
    }

    [Fact]
    public async Task An_unknown_job_or_window_is_not_found()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/copilot/explain", new CopilotRequest("nope", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/copilot/explain",
                new CopilotRequest(new string('a', 32), 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/copilot/explain", new CopilotRequest(jobId, -1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/copilot/explain", new CopilotRequest(jobId, 9999))).StatusCode);
    }

    [Fact]
    public async Task A_remote_copilot_address_is_refused_by_the_endpoint_too()
    {
        await using var factory = new AuthedFactory()
            .WithWebHostBuilder(b => b
                .UseSetting("Jobs:RootPath", _root)
                .UseSetting("Models:RootPath", _modelsRoot)
                .UseSetting("Models:AutoDownload", "false")
                .UseSetting("Copilot:BaseUrl", "https://api.openai.com"));
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        var reply = await (await client.PostAsJsonAsync("/api/copilot/explain", new CopilotRequest(jobId, 0)))
            .Content.ReadFromJsonAsync<CopilotReply>();

        // Spec §5: the copilot is never cloud. Configuration cannot turn it into one.
        Assert.False(reply!.Available);
        Assert.Contains("local", reply.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
