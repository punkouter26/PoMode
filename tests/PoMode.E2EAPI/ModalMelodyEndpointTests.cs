using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class ModalMelodyEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-modal-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2e-models-{Guid.NewGuid():N}");

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
    public async Task ModalMelody_Generate_and_WavExport_Endpoints_ReturnValidData()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var request = new ModalMelodyRequest(0, ScaleMode.Dorian, "pop-axis", 110.0, MelodyStyle.Lyrical, 42, 90);
        var genResponse = await client.PostAsJsonAsync("/api/modal-melodies/generate", request);
        genResponse.EnsureSuccessStatusCode();

        var generated = await genResponse.Content.ReadFromJsonAsync<GeneratedMelodyDto>();
        Assert.NotNull(generated);
        Assert.Equal(ScaleMode.Dorian, generated.Mode);

        var wavResponse = await client.GetAsync(
            $"/api/modal-melodies/wav?TonicPitchClass=0&Mode=Dorian&ProgressionId=pop-axis&Bpm=110&Style=Lyrical&Seed=42&TargetPurity=90");
        wavResponse.EnsureSuccessStatusCode();
        Assert.Equal("audio/wav", wavResponse.Content.Headers.ContentType?.MediaType);
        var wavBytes = await wavResponse.Content.ReadAsByteArrayAsync();
        Assert.True(wavBytes.Length > 44);
    }

    [Fact]
    public async Task ModalMelody_AnalyzeBridge_QueuesAnalysisJob()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var request = new ModalMelodyRequest(0, ScaleMode.Ionian, "pop-axis", 120.0, MelodyStyle.Lyrical, 42, 95);
        var response = await client.PostAsJsonAsync("/api/modal-melodies/analyze", request);
        response.EnsureSuccessStatusCode();

        var job = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(job);
        Assert.False(string.IsNullOrWhiteSpace(job.JobId));
    }
}
