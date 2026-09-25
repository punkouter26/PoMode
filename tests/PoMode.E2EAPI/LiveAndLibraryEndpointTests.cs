using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class LiveAndLibraryEndpointTests : IClassFixture<AuthedFactory>
{
    private readonly AuthedFactory _factory;

    public LiveAndLibraryEndpointTests(AuthedFactory factory) => _factory = factory;

    /// <summary>The library is the caller's own: an upload shows up for its owner, is invisible to
    /// every other user, and the listing refuses anyone without a session.</summary>
    [Fact]
    public async Task Library_lists_only_the_callers_jobs_and_requires_auth()
    {
        using var client = _factory.CreateClient();
        var audio = new ByteArrayContent(TestAudio.MakeWav());
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent { { audio, "file", "mine.wav" } };
        var created = await (await client.PostAsync("/api/analysis", form)).Content.ReadFromJsonAsync<JobStatusDto>();

        var mine = await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library");
        Assert.Contains(mine!, e => e.JobId == created!.JobId);

        using var asSomeoneElse = new HttpRequestMessage(HttpMethod.Get, "/api/library");
        asSomeoneElse.Headers.Add("X-Fake-User", "someone-else");
        var theirs = await (await client.SendAsync(asSomeoneElse)).Content.ReadFromJsonAsync<List<LibraryEntryDto>>();
        Assert.DoesNotContain(theirs!, e => e.JobId == created!.JobId);

        await using var plain = new WebApplicationFactory<Program>();
        using var anonymous = plain.CreateClient();
        var response = await anonymous.GetAsync("/api/library");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
