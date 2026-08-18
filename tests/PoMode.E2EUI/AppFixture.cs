using System.Diagnostics;
using Xunit;

namespace PoMode.E2EUI;

/// <summary>Boots the real PoMode.API (which hosts the WASM client) for browser tests.</summary>
public class AppFixture : IAsyncLifetime
{
    private readonly int _port;
    private readonly string _jobsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2eui-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2eui-models-{Guid.NewGuid():N}");
    private Process? _server;

    public AppFixture() : this(5199)
    {
    }

    protected AppFixture(int port)
    {
        _port = port;
    }

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>The isolated models directory this fixture's server reads. Derived fixtures may seed it.</summary>
    protected string ModelsRoot => _modelsRoot;

    /// <summary>Extra environment variables for the server process. Base adds none.</summary>
    protected virtual void ConfigureEnvironment(IDictionary<string, string?> environment)
    {
    }

    /// <summary>Runs before the server starts — a derived fixture's chance to seed <see cref="ModelsRoot"/>.</summary>
    protected virtual Task BeforeServerStartAsync() => Task.CompletedTask;

    /// <summary>Blazor WASM cold-boot can exceed 30 s when the whole solution's test assemblies run in parallel.</summary>
    public const float ExpectTimeoutMs = 60000f;

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        await BeforeServerStartAsync();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project src/PoMode.API --urls {BaseUrl}",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["Jobs__RootPath"] = _jobsRoot,
                // Browser tests assert on FakePitchTracker's deterministic output; a real model download
                // completing mid-suite would flip ExecutionPlanner onto the (non-deterministic here) local
                // tier and also make these tests depend on network access. Models:RootPath is isolated
                // too (not just AutoDownload=false) so a stray .onnx file already sitting in the shared
                // build output from an earlier run can never make IsAvailableAsync see it and flip
                // ExecutionPlanner regardless of the auto-download setting.
                ["Models__AutoDownload"] = "false",
                ["Models__RootPath"] = _modelsRoot,
                // Headless Chromium honestly declares WASM inference capability, and a capable
                // browser outranks FakePitchTracker — which would make every test here run real
                // (network-downloading, non-deterministic) in-browser inference. These tests assert
                // the fake pipeline's deterministic output, so the browser tier is switched off;
                // ClientDelegatedFlowTests covers Tier 2 on its own fixture with it switched on.
                ["Tier2__Enabled"] = "false",
            },
        };
        ConfigureEnvironment(startInfo.Environment);
        _server = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start PoMode.API");

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

        try
        {
            if (Directory.Exists(_jobsRoot)) Directory.Delete(_jobsRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup — a leaked temp dir must never fail a test
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup — a leaked temp dir must never fail a test
        }

        try
        {
            if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup — a leaked temp dir must never fail a test
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup — a leaked temp dir must never fail a test
        }

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
