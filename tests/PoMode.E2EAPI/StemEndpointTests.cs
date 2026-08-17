using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class StemEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-stems-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-stems-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

    private static async Task<string> CompletedJobAsync(HttpClient client)
    {
        var content = new ByteArrayContent(TestAudio.MakeWav());
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

    [Theory]
    [InlineData("mix")]
    [InlineData("vocals")]
    [InlineData("instrumental")]
    public async Task Each_allow_listed_stem_is_served_as_playable_audio(string name)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        var response = await client.GetAsync($"/api/analysis/{jobId}/stems/{name}");

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 44, $"{name} was only {bytes.Length} bytes");
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]); // the fixture is a wav, and so are the stems
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_unknown_stem_name_is_not_found_rather_than_a_server_error()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        // Anything that reaches the endpoint as a single path segment but is not on the allow-list.
        // "vocals.wav" and "notes.json" are real files in the job directory, so they prove the name
        // is matched against the allow-list rather than used to build a file path.
        foreach (var name in new[] { "drums", "bass", "job", "result", "notes.json", "vocals.wav" })
        {
            var response = await client.GetAsync($"/api/analysis/{jobId}/stems/{name}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Traversal_attempts_never_return_audio_or_job_state()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        // Some of these do not match the stems route at all and therefore land on the Blazor SPA
        // fallback (a 200 with index.html) — that is correct hosting behaviour, not a leak. The
        // property that matters is that none of them ever yields audio bytes or the job state, so
        // that is what this asserts rather than a status code.
        string[] attempts =
        [
            "../job.json",
            "..%2Fjob.json",
            "..%252Fjob.json",
            "%2e%2e%2fjob.json",
            "....//job.json",
            "",
        ];
        foreach (var attempt in attempts)
        {
            var response = await client.GetAsync($"/api/analysis/{jobId}/stems/{attempt}");

            Assert.DoesNotContain("audio", response.Content.Headers.ContentType?.MediaType ?? "");
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("RIFF", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"jobId\"", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("InputFileName", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task An_unknown_job_is_not_found()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/analysis/nope/stems/mix")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/analysis/{new string('a', 32)}/stems/mix")).StatusCode);
    }
}
