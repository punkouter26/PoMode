using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.API.Features.Demo;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

/// <summary>
/// The first-run demo: the template is built once through the real pipeline, and each new library is
/// handed a finished copy of it — once, so losing the copy does not bring it back.
/// </summary>
public sealed class DemoSongTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-demo-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2e-demo-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    [Fact]
    public async Task A_new_library_gets_one_finished_copy_of_the_demo_and_never_a_second()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Jobs:Storage:Mode", "FileSystem")
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false")
            .UseSetting("RateLimits:Enabled", "false")
            .UseSetting("Demo:Enabled", "true"));

        using var first = await GuestAsync(factory);
        // The template builds after startup; until it is finished the library is simply empty.
        var demo = await WaitForDemoAsync(first);
        Assert.Equal(JobStage.Complete, demo.Stage);
        Assert.NotEqual(DemoSong.TemplateJobId, demo.JobId);
        // A copy is finished work, artifacts and all: the canvas payload answers with no run of its own.
        (await first.GetAsync($"/api/analysis/{demo.JobId}/visual")).EnsureSuccessStatusCode();

        // Losing the copy — here the folder, as the 7-day sweep would take it — does not reseed.
        Directory.Delete(Path.Combine(_root, demo.JobId), recursive: true);
        Assert.Empty((await first.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library"))!);

        // The next newcomer gets a copy of their own at once, now that the template exists.
        using var second = await GuestAsync(factory);
        var theirs = Assert.Single((await second.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library"))!);
        Assert.Equal(TakeOrigin.Demo, theirs.Origin?.Kind);
        Assert.NotEqual(demo.JobId, theirs.JobId);
    }

    /// <summary>A browser's first visit: its own cookie jar, and a guest session minted into it.</summary>
    private static async Task<HttpClient> GuestAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsync("/auth/guest", null)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<LibraryEntryDto> WaitForDemoAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var library = await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library");
            if (library is { Count: > 0 })
            {
                var entry = Assert.Single(library);
                Assert.Equal(TakeOrigin.Demo, entry.Origin?.Kind);
                return entry;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("The demo template never finished building.");
    }
}
