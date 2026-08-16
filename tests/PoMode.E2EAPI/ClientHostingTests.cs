using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PoMode.E2EAPI;

public class ClientHostingTests
{
    [Fact]
    public async Task Root_serves_blazor_index_html()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("blazor.webassembly.js", html);
        Assert.Contains("<title>PoMode</title>", html);
    }
}
