using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class ClientHostingTests : IDisposable
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
    public async Task Root_serves_blazor_index_html()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("blazor.webassembly.js", html);
        Assert.Contains("<title>PoMode</title>", html);
    }
}
