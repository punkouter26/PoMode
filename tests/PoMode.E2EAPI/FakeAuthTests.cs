using System.Linq;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using PoMode.Shared.Session;

namespace PoMode.E2EAPI;

public sealed class FakeAuthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:AutoDownload", "false"));

    [Fact]
    public async Task Session_without_fake_user_header_returns_401()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Session_with_fake_user_and_roles_returns_identity()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", "alice");
        client.DefaultRequestHeaders.Add("X-Fake-Roles", "admin, listener");

        var session = await client.GetFromJsonAsync<SessionInfo>("/api/session");

        Assert.NotNull(session);
        Assert.Equal("alice", session.UserName);
        Assert.Equal(["admin", "listener"], session.Roles);
    }

    [Fact]
    public void FakeAuth_throws_InvalidOperationException_in_production()
    {
        using var factory = Factory()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("FakeAuthHandler must never run in Production",
            FlattenMessages(exception));
    }

    private static string FlattenMessages(Exception ex) =>
        ex is AggregateException agg
            ? string.Join(" | ", agg.InnerExceptions.Select(FlattenMessages))
            : ex.Message + (ex.InnerException is { } inner ? " | " + FlattenMessages(inner) : "");
}
