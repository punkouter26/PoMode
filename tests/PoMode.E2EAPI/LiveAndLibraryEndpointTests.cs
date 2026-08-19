using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class LiveAndLibraryEndpointTests : IClassFixture<AuthedFactory>
{
    private readonly AuthedFactory _factory;

    public LiveAndLibraryEndpointTests(AuthedFactory factory) => _factory = factory;

    [Fact]
    public async Task Live_analyze_returns_a_result_and_visual_for_valid_notes()
    {
        using var client = _factory.CreateClient();
        // A C major scale over eight seconds: enough distinct pitch classes for a mode verdict.
        var notes = new[] { 60, 62, 64, 65, 67, 69, 71, 72 }
            .Select((pitch, index) => new NoteEvent(pitch, index * 1.0, 0.8, 90))
            .ToList();

        var response = await client.PostAsJsonAsync("/api/live/analyze", new LiveAnalyzeRequest(notes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var analysis = await response.Content.ReadFromJsonAsync<LiveAnalysisDto>();
        Assert.NotNull(analysis);
        Assert.Equal(notes.Count, analysis!.Visual.Notes.Count);
        Assert.NotEmpty(analysis.Result.Windows);
        Assert.False(string.IsNullOrEmpty(analysis.Result.TonicName));
    }

    [Fact]
    public async Task Library_lists_jobs_and_requires_auth()
    {
        using var client = _factory.CreateClient();
        var entries = await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library");
        Assert.NotNull(entries); // empty is fine on a fresh test host — the contract is the shape

        await using var plain = new WebApplicationFactory<Program>();
        using var anonymous = plain.CreateClient();
        var response = await anonymous.GetAsync("/api/library");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
