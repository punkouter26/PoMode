using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class BatchAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-batch-{Guid.NewGuid():N}");
    private readonly string _batchRoot = Path.Combine(Path.GetTempPath(), $"pomode-batchm-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-batch-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_batchRoot)) Directory.Delete(_batchRoot, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Jobs:BatchPath", _batchRoot)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

    private static MultipartFormDataContent TwoTrackForm()
    {
        var first = new ByteArrayContent(TestAudio.MakeWav());
        first.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var second = new ByteArrayContent(TestAudio.MakeWav());
        second.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new MultipartFormDataContent
        {
            { first, "files", "one.wav" },
            { second, "files", "two.wav" },
        };
    }

    [Fact]
    public async Task Batch_runs_both_tracks_and_serves_one_zip()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var form = TwoTrackForm();
        var createResponse = await client.PostAsync("/api/analysis/batch", form);
        createResponse.EnsureSuccessStatusCode();
        var batch = await createResponse.Content.ReadFromJsonAsync<BatchStatusDto>();
        Assert.Equal(2, batch!.Tracks.Count);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            batch = await client.GetFromJsonAsync<BatchStatusDto>($"/api/analysis/batch/{batch!.BatchId}");
            if (batch!.Tracks.All(t => t.Stage is JobStage.Complete or JobStage.Failed or JobStage.Cancelled))
            {
                break;
            }
            await Task.Delay(200);
        }
        Assert.All(batch!.Tracks, track => Assert.Equal(JobStage.Complete, track.Stage));

        var zipResponse = await client.GetAsync($"/api/analysis/batch/{batch.BatchId}/zip");

        zipResponse.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", zipResponse.Content.Headers.ContentType!.MediaType);
        var bytes = await zipResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    [Fact]
    public async Task Batch_survives_a_restart_because_the_manifest_is_persisted()
    {
        string batchId;
        await using (var factory = Factory())
        {
            using var client = factory.CreateClient();
            using var form = TwoTrackForm();
            var created = await (await client.PostAsync("/api/analysis/batch", form))
                .Content.ReadFromJsonAsync<BatchStatusDto>();
            batchId = created!.BatchId;
        }

        // A brand-new host (same folders) must still know the batch — PoModeAg lost these.
        await using (var factory = Factory())
        {
            using var client = factory.CreateClient();
            var status = await client.GetFromJsonAsync<BatchStatusDto>($"/api/analysis/batch/{batchId}");
            Assert.Equal(2, status!.Tracks.Count);
        }
    }

    [Fact]
    public async Task Empty_batch_is_400_and_unknown_batch_is_404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var empty = new MultipartFormDataContent();
        empty.Add(new StringContent("x"), "not-a-file");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/analysis/batch", empty)).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/analysis/batch/00000000000000000000000000000000")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/analysis/batch/nope")).StatusCode);
    }
}
