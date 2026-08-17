using Microsoft.AspNetCore.Mvc.Testing;

namespace PoMode.E2EAPI;

/// <summary>Factory whose clients present the FakeAuth headers, matching the Blazor client's
/// defaults — the write endpoints (cancel, client-result, copilot, from-url) require them.</summary>
public sealed class AuthedFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-Fake-User", "e2e");
        client.DefaultRequestHeaders.Add("X-Fake-Roles", "listener");
    }
}
