using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class MusicXmlExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-mxl-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-mxl-models-{Guid.NewGuid():N}");

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

    [Fact]
    public async Task Completed_job_exports_parseable_musicxml_with_stage_history()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var content = new ByteArrayContent(TestAudio.MakeWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent { { content, "file", "test.wav" } };
        var created = await (await client.PostAsync("/api/analysis", form)).Content.ReadFromJsonAsync<JobStatusDto>();

        JobStatusDto? status = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{created!.JobId}");
            if (status!.Stage is JobStage.Complete or JobStage.Failed) break;
            await Task.Delay(200);
        }

        Assert.Equal(JobStage.Complete, status!.Stage);
        // Concept #7: every finished stage leaves an audit record with a completion time.
        Assert.NotNull(status.StageHistory);
        Assert.Equal(4, status.StageHistory!.Count);
        Assert.All(status.StageHistory, record => Assert.NotNull(record.CompletedAt));

        var response = await client.GetAsync($"/api/analysis/{created!.JobId}/musicxml");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/vnd.recordare.musicxml+xml",
            response.Content.Headers.ContentType!.MediaType);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("score-partwise", xml.Root!.Name.LocalName);
        Assert.NotEmpty(xml.Descendants("note"));
    }

    [Fact]
    public async Task Unknown_or_malformed_job_musicxml_is_404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope/musicxml")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/analysis/00000000000000000000000000000000/musicxml")).StatusCode);
    }
}
