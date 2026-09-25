using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class AuthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2e-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    /// <summary>
    /// Production boots with guest sign-in and no header auth: the FakeAuth scheme is not registered
    /// there, so <c>X-Fake-User</c> is an ordinary header, while a guest session is a real one whose
    /// cookie opens the caller's own library.
    /// </summary>
    [Fact]
    public async Task Production_ignores_fake_headers_but_accepts_a_guest_session()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseEnvironment("Production")
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Jobs:Storage:Mode", "FileSystem")
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        using var faked = new HttpRequestMessage(HttpMethod.Get, "/api/library");
        faked.Headers.Add("X-Fake-User", "mallory");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(faked)).StatusCode);

        var guest = await (await client.PostAsync("/auth/guest", null)).Content.ReadFromJsonAsync<SessionDto>();
        Assert.Equal(SessionKind.Guest, guest?.Kind);

        var library = await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library");
        Assert.NotNull(library);
        var session = await client.GetFromJsonAsync<SessionDto>("/api/auth/session");
        Assert.Equal(guest!.DisplayName, session?.DisplayName);
    }
}
