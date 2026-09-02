using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class ClientResultTests : IDisposable
{
    private const string JobId = "abcdef0123456789abcdef0123456789";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-clientres-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-clientres-models-{Guid.NewGuid():N}");

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

    private static Task<IReadOnlyList<NoteEvent>> ParkAsync(WebApplicationFactory<Program> factory)
    {
        var registry = factory.Services.GetRequiredService<ClientWorkRegistry>();
        return registry.WaitAsync(JobId, TimeSpan.FromMinutes(5), CancellationToken.None);
    }

    [Fact]
    public async Task A_valid_payload_is_accepted_and_satisfies_the_parked_stage()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var parked = ParkAsync(factory);

        NoteEvent[] notes = [new(60, 0.0, 0.5, 90), new(64, 0.5, 0.5, 88)];
        var response = await client.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", notes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivered = await parked;
        Assert.Equal(2, delivered.Count);
        Assert.Equal(60, delivered[0].MidiPitch);
    }

    [Fact]
    public async Task Invalid_payload_and_unparked_job_are_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        _ = ParkAsync(factory);

        var badResponse = await client.PostAsJsonAsync(
            $"/api/analysis/{JobId}/client-result",
            new[] { new NoteEvent(109, 0.0, 0.5, 90) });
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        var unparkedResponse = await client.PostAsJsonAsync(
            $"/api/analysis/00000000000000000000000000000000/client-result",
            new[] { new NoteEvent(60, 0.0, 0.5, 90) });
        Assert.Equal(HttpStatusCode.NotFound, unparkedResponse.StatusCode);
    }
}
