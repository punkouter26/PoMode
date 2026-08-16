using System.Diagnostics;
using Xunit;

namespace PoMode.E2EUI;

/// <summary>Boots the real PoMode.API (which hosts the WASM client) for browser tests.</summary>
public class AppFixture : IAsyncLifetime
{
    private readonly int _port;
    private Process? _server;

    public AppFixture() : this(5199)
    {
    }

    protected AppFixture(int port)
    {
        _port = port;
    }

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        _server = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project src/PoMode.API --urls {BaseUrl}",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
        }) ?? throw new InvalidOperationException("Failed to start PoMode.API");

        using var http = new HttpClient();
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                var response = await http.GetAsync($"{BaseUrl}/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // server not up yet
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("PoMode.API did not become healthy within 60s.");
    }

    public Task DisposeAsync()
    {
        if (_server is { HasExited: false })
        {
            _server.Kill(entireProcessTree: true);
        }
        _server?.Dispose();
        return Task.CompletedTask;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoMode.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("PoMode.slnx not found above test bin dir.");
    }
}

[CollectionDefinition("App")]
public sealed class AppCollection : ICollectionFixture<AppFixture>;

/// <summary>Isolated app instance for the large-upload test so its long-running job cannot
/// starve the single-worker queue used by the browser tests on the "App" collection.</summary>
public sealed class LargeUploadAppFixture : AppFixture
{
    public LargeUploadAppFixture() : base(5200)
    {
    }
}

[CollectionDefinition("LargeUploadApp")]
public sealed class LargeUploadAppCollection : ICollectionFixture<LargeUploadAppFixture>;
