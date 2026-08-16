using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class AnalysisApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.UseSetting("Jobs:RootPath", _root));

    private static MultipartFormDataContent WavForm(byte[]? bytes = null)
    {
        var content = new ByteArrayContent(bytes ?? TestAudio.MakeWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new MultipartFormDataContent { { content, "file", "test.wav" } };
    }

    [Fact]
    public async Task Upload_returns_job_status_and_job_completes_via_hub_or_polling()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, "/hubs/analysis"),
                options => options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler())
            .Build();
        var terminal = new TaskCompletionSource<JobStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JobStatusDto>("JobStatusChanged", status =>
        {
            if (status.Stage is JobStage.Complete or JobStage.Failed) terminal.TrySetResult(status);
        });
        await hub.StartAsync();

        using var form = WavForm();
        var response = await client.PostAsync("/api/analysis", form);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(created);
        Assert.Equal(4, created.Plan.Count);
        await hub.InvokeAsync("Subscribe", created.JobId);

        // The fake pipeline may finish before Subscribe lands — poll as a fallback.
        var final = await WaitForTerminalAsync(client, created.JobId, terminal.Task);
        Assert.Equal(JobStage.Complete, final.Stage);

        var notes = await client.GetFromJsonAsync<List<NoteEvent>>($"/api/analysis/{created.JobId}/notes");
        var chords = await client.GetFromJsonAsync<List<ChordSpan>>($"/api/analysis/{created.JobId}/chords");
        Assert.Equal(8, notes!.Count);
        Assert.Equal(4, chords!.Count);
    }

    private static async Task<JobStatusDto> WaitForTerminalAsync(
        HttpClient client, string jobId, Task<JobStatusDto> hubSignal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (hubSignal.IsCompleted) return await hubSignal;
            var status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{jobId}");
            if (status!.Stage is JobStage.Complete or JobStage.Failed or JobStage.Cancelled) return status;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Job {jobId} did not reach a terminal stage in 15s.");
    }

    [Fact]
    public async Task Upload_without_file_is_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/analysis", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_of_non_audio_content_is_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var form = WavForm([0x25, 0x50, 0x44, 0x46, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        var response = await client.PostAsync("/api/analysis", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("supported", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Status_of_unknown_job_is_404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/analysis/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope/notes")).StatusCode);
    }

    [Fact]
    public async Task Traversal_style_job_ids_are_rejected_with_404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/..%2F..%2Fsecrets/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/C%3Afoo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/analysis/ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")).StatusCode);
    }

    private sealed class UnavailableStemSeparator : PoMode.API.Pipeline.IStemSeparator
    {
        public string Name => nameof(UnavailableStemSeparator);
        public PoMode.Shared.Analysis.ExecutionTier Tier => PoMode.Shared.Analysis.ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(false);
        public Task SeparateAsync(PoMode.API.Pipeline.StageContext context, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Upload_with_no_available_executor_returns_failed_job_not_500()
    {
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<PoMode.API.Pipeline.IStemSeparator>();
            services.AddSingleton<PoMode.API.Pipeline.IStemSeparator, UnavailableStemSeparator>();
        }));
        using var client = factory.CreateClient();

        using var form = WavForm();
        var response = await client.PostAsync("/api/analysis", form);

        response.EnsureSuccessStatusCode(); // NOT a 500
        var status = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.Equal(JobStage.Failed, status!.Stage);
        Assert.Contains("Separating", status.Error);
    }
}
