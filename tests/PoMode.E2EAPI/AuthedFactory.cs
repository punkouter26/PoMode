using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace PoMode.E2EAPI;

/// <summary>Factory whose clients present the FakeAuth headers, matching the Blazor client's
/// defaults — the write endpoints (cancel, client-result, from-url) require them.</summary>
public sealed class AuthedFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Every test in this assembly shares one in-process server and one partition key, and they
        // drive the endpoints at machine speed — so the per-minute limits would be measuring the
        // test runner rather than the app. Switched off rather than raised, because a limit set high
        // enough for a test suite protects nothing in production.
        builder.UseSetting("RateLimits:Enabled", "false");
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-Fake-User", "e2e");
        client.DefaultRequestHeaders.Add("X-Fake-Roles", "listener");
    }
}
