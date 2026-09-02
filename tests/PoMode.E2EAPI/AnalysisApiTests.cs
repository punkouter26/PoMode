using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class AnalysisApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2e-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new AuthedFactory()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

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

        var final = await WaitForTerminalAsync(client, created.JobId, terminal.Task);
        Assert.Equal(JobStage.Complete, final.Stage);

        var result = await client.GetFromJsonAsync<ModalResult>($"/api/analysis/{created.JobId}/result");
        Assert.NotNull(result);
        Assert.Equal(1, result.SchemaVersion);
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
    public async Task Upload_input_validation_guards_non_audio_and_traversal_ids()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var form = WavForm([0x25, 0x50, 0x44, 0x46, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        var response = await client.PostAsync("/api/analysis", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/..%2F..%2Fsecrets/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/C%3Afoo")).StatusCode);
    }

    private sealed class UnavailableStemSeparator : PoMode.API.Pipeline.IStemSeparator
    {
        public string Name => nameof(UnavailableStemSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
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

        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.Equal(JobStage.Failed, status!.Stage);
        Assert.Contains("Separating", status.Error);
    }
}
