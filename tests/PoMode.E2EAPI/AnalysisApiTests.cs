using System.Globalization;
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
        if (Directory.Exists(_root + "-uploads")) Directory.Delete(_root + "-uploads", recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new AuthedFactory()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

    /// <summary>The phone-on-a-train case, end to end: half a memo arrives, the connection drops, the
    /// client asks how far the server got and sends only the rest — and at no point can anyone else
    /// see the upload, or can it become two jobs. Then the job runs to completion.</summary>
    [Fact]
    public async Task Dropped_upload_resumes_privately_starts_one_job_and_it_completes_via_hub_or_polling()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var stranger = factory.CreateClient();
        stranger.DefaultRequestHeaders.Remove("X-Fake-User");
        stranger.DefaultRequestHeaders.Add("X-Fake-User", "someone-else");

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

        var audio = TestAudio.MakeWav();
        var half = audio.Length / 2;
        var uploadId = await UploadClientExtensions.CreateUploadAsync(client, audio.Length, "memo.wav", audio[..half]);
        var path = $"/api/uploads/{uploadId}";

        // Half the bytes are not a song: starting the analysis now is refused, not guessed at.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/analysis/uploads/{uploadId}", null)).StatusCode);
        // Resuming starts with a HEAD: the server says how much it holds.
        var offset = long.Parse(Assert.Single((await TusHead(client, path)).Headers.GetValues("Upload-Offset")), CultureInfo.InvariantCulture);
        Assert.Equal(half, offset);
        // Someone else cannot probe it, finish it, or start an analysis of it.
        Assert.Equal(HttpStatusCode.NotFound, (await TusHead(stranger, path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TusPatch(stranger, path, offset, audio[half..])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/analysis/uploads/{uploadId}", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await TusPatch(client, path, offset, audio[half..])).StatusCode);
        var response = await client.PostAsync($"/api/analysis/uploads/{uploadId}?clientCanInfer=false", null);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(created);
        Assert.Equal("memo.wav", created.FileName);
        Assert.Equal(4, created.Plan.Count);
        await hub.InvokeAsync("Subscribe", created.JobId);

        // A client whose response was lost retries; it gets the same job, not a second one. The bytes
        // have moved into the job, so the upload itself is gone.
        var retried = await (await client.PostAsync($"/api/analysis/uploads/{uploadId}", null)).Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.Equal(created.JobId, retried!.JobId);
        Assert.Equal(HttpStatusCode.NotFound, (await TusHead(client, path)).StatusCode);

        var final = await WaitForTerminalAsync(client, created.JobId, terminal.Task);
        Assert.Equal(JobStage.Complete, final.Stage);

        var result = await client.GetFromJsonAsync<ModalResult>($"/api/analysis/{created.JobId}/result");
        Assert.NotNull(result);
        Assert.Equal(1, result.SchemaVersion);
    }

    private static Task<HttpResponseMessage> TusHead(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, path);
        request.Headers.Add("Tus-Resumable", UploadClientExtensions.TusVersion);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> TusPatch(HttpClient client, string path, long offset, byte[] bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = new ByteArrayContent(bytes) };
        request.Headers.Add("Tus-Resumable", UploadClientExtensions.TusVersion);
        request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        return client.SendAsync(request);
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

        var response = await client.UploadAudioAsync([0x25, 0x50, 0x44, 0x46, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
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

        var response = await client.UploadAudioAsync(TestAudio.MakeWav());

        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.Equal(JobStage.Failed, status!.Stage);
        Assert.Contains("Separating", status.Error);
    }
}
