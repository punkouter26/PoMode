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
