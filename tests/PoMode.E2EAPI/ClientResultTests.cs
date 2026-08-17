using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

/// <summary>
/// The Tier-2 return path over real HTTP. The registry is driven directly rather than by running a
/// parked job, so these tests pin the endpoint's contract — who may post, and what is accepted —
/// without depending on pipeline timing.
/// </summary>
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

    /// <summary>Parks a waiter for <see cref="JobId"/> and returns the task the endpoint should satisfy.</summary>
    private static Task<IReadOnlyList<NoteEvent>> ParkAsync(WebApplicationFactory<Program> factory)
    {
        var registry = factory.Services.GetRequiredService<ClientWorkRegistry>();
        return registry.WaitAsync(JobId, TimeSpan.FromMinutes(5), CancellationToken.None);
    }

    private static NoteEvent[] GoodNotes() =>
    [
        new NoteEvent(60, 0.0, 0.5, 90),
        new NoteEvent(64, 0.5, 0.5, 88),
    ];

    [Fact]
    public async Task A_valid_payload_is_accepted_and_satisfies_the_parked_stage()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var parked = ParkAsync(factory);

        var response = await client.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", GoodNotes());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivered = await parked;
        Assert.Equal(2, delivered.Count);
        Assert.Equal(60, delivered[0].MidiPitch);
    }

    [Fact]
    public async Task Posting_for_a_job_nobody_is_waiting_on_is_not_found()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", GoodNotes());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_job_id_is_not_found()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/analysis/nope/client-result", GoodNotes());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_second_post_is_refused_because_the_waiter_is_gone()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var parked = ParkAsync(factory);

        var first = await client.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", GoodNotes());
        var second = await client.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", GoodNotes());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        await parked;
    }

    [Fact]
    public async Task An_out_of_range_pitch_is_rejected_with_a_reason_and_the_stage_stays_parked()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var parked = ParkAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/analysis/{JobId}/client-result",
            new[] { new NoteEvent(200, 0.0, 0.5, 90) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("pitch", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        // A rejected payload must not consume the waiter — the browser can correct itself and retry.
        Assert.False(parked.IsCompleted);
    }

    [Fact]
    public async Task A_rejected_payload_can_be_followed_by_a_good_one()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var parked = ParkAsync(factory);

        await client.PostAsJsonAsync(
            $"/api/analysis/{JobId}/client-result", new[] { new NoteEvent(60, -5.0, 0.5, 90) });
        var retry = await client.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", GoodNotes());

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, (await parked).Count);
    }

    [Fact]
    public async Task A_flood_of_notes_is_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var parked = ParkAsync(factory);

        var flood = Enumerable.Range(0, 20_001)
            .Select(_ => new NoteEvent(60, 0.0, 0.5, 90))
            .ToArray();
        var response = await client.PostAsJsonAsync($"/api/analysis/{JobId}/client-result", flood);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(parked.IsCompleted);
    }

    [Fact]
    public async Task An_empty_payload_is_accepted_because_silence_really_has_no_notes()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var parked = ParkAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"/api/analysis/{JobId}/client-result", Array.Empty<NoteEvent>());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await parked);
    }
}
