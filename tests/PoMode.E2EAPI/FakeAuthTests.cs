using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using PoMode.Shared.Session;

namespace PoMode.E2EAPI;

public class FakeAuthTests
{
    [Fact]
    public async Task Session_without_fake_user_header_returns_401()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Session_with_fake_user_and_roles_returns_identity()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", "alice");
        client.DefaultRequestHeaders.Add("X-Fake-Roles", "admin, listener");

        var session = await client.GetFromJsonAsync<SessionInfo>("/api/session");

        Assert.NotNull(session);
        Assert.Equal("alice", session.UserName);
        Assert.Equal(["admin", "listener"], session.Roles);
    }

    [Fact]
    public async Task FakeAuth_throws_InvalidOperationException_in_production()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", "mallory");

        // TestServer rethrows unhandled server exceptions to the caller.
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/api/session"));
    }
}
