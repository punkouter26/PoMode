using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;
using PoMode.API.Infrastructure;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

/// <summary>
/// Boots the app in simulated Azure mode — Tier 2's real target scenario, where §4 hard-disables
/// local ONNX — with the browser runtime pre-seeded so the run needs no network. Local tiers are
/// unavailable by environment, so a capable browser is the best pitch executor.
/// </summary>
public sealed class ClientDelegatedAppFixture : AppFixture
{
    public ClientDelegatedAppFixture() : base(5201)
    {
    }

    protected override void ConfigureEnvironment(IDictionary<string, string?> environment)
    {
        // Azure mode: OnnxStemSeparator/OnnxPitchTracker report unavailable, ModelRegistry refuses
        // downloads, and /web-runtime serves only what is already on disk — which is exactly what
        // BeforeServerStartAsync put there.
        environment["WEBSITE_INSTANCE_ID"] = "pomode-e2eui-azure-sim";
        // The base fixture switches the browser tier off to keep the other browser tests
        // deterministic; this fixture exists to exercise it.
        environment["Tier2__Enabled"] = "true";
    }

    /// <summary>
    /// Seeds the isolated models directory with the browser runtime + model. Fast path: copy from
    /// the repo's git-ignored <c>models/</c> cache. Slow path (fresh clone): download from the
    /// pinned catalog URLs — and cache into <c>models/</c> so only the first run on a machine pays.
    /// Every byte is SHA-256-verified against the same catalog the server enforces.
    /// </summary>
    protected override async Task BeforeServerStartAsync()
    {
        Directory.CreateDirectory(ModelsRoot);
        var repoCache = Path.Combine(FindRepoRootForSeed(), "models");
        Directory.CreateDirectory(repoCache);
        using var http = new HttpClient();

        foreach (var descriptor in ModelCatalog.WebRuntime)
        {
            var cached = Path.Combine(repoCache, descriptor.FileName);
            if (!File.Exists(cached) || !await HashMatchesAsync(cached, descriptor.Sha256))
            {
                var bytes = await http.GetByteArrayAsync(descriptor.Url);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Downloaded {descriptor.Key} failed SHA-256 verification: expected {descriptor.Sha256}, got {hash}.");
                }
                await File.WriteAllBytesAsync(cached, bytes);
            }
            File.Copy(cached, Path.Combine(ModelsRoot, descriptor.FileName), overwrite: true);
        }
    }

    private static async Task<bool> HashMatchesAsync(string path, string expectedSha256)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        return hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRootForSeed()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoMode.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("PoMode.slnx not found above test bin dir.");
    }
}

[CollectionDefinition("ClientDelegatedApp")]
public sealed class ClientDelegatedAppCollection : ICollectionFixture<ClientDelegatedAppFixture>;

/// <summary>
/// The Phase 8 exit criterion, end to end: a job pitch-tracked *by the browser*. Real WASM-SIMD
/// inference on the real pinned Basic Pitch model — WebGPU cannot be exercised in headless
/// Chromium on this machine (measured in the Phase 8 plan), so the WASM path is what is proven.
/// </summary>
[Collection("ClientDelegatedApp")]
public class ClientDelegatedFlowTests(ClientDelegatedAppFixture app)
{
    /// <summary>Model download + first WASM inference in a cold browser can be slow; be generous.</summary>
    private const float FlowTimeoutMs = 180000f;

    [Fact]
    public async Task The_browser_transcribes_the_vocals_and_the_job_completes_on_the_client_tier()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        // The worker module deliberately swallows its own errors (the server timeout owns failure
        // handling), so the console is the only witness when delegation breaks — collect it.
        var console = new List<string>();
        page.Console += (_, msg) => console.Add($"[{msg.Type}] {msg.Text}");
        page.PageError += (_, err) => console.Add($"[pageerror] {err}");
        await page.GotoAsync(app.BaseUrl);

        // Wait for the capability probe so the upload declares clientCanInfer. Headless Chromium
        // here has no WebGPU adapter, so the honest expectation is the WASM path.
        var capabilityMarker = page.Locator("span.client-capability");
        await Assertions.Expect(capabilityMarker).ToHaveAttributeAsync(
            "data-capability", new System.Text.RegularExpressions.Regex("^(wasm|webgpu)$"),
            new() { Timeout = AppFixture.ExpectTimeoutMs });
        Assert.Equal("wasm", await capabilityMarker.GetAttributeAsync("data-capability"));

        // A 440 Hz tone: unambiguous ground truth — Basic Pitch must land on A4 (MIDI 69).
        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-tier2-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, TestAudio.MakeTone(seconds: 3.0, frequencyHz: 440.0, sampleRate: 22050));
        string jobId;
        try
        {
            var uploadResponse = await page.RunAndWaitForResponseAsync(
                () => page.Locator("input[type=file]").SetInputFilesAsync(wavPath),
                r => r.Url.Contains("/api/analysis") && r.Request.Method == "POST",
                new() { Timeout = AppFixture.ExpectTimeoutMs });

            // The upload itself must have declared the capability, or the plan below proves nothing.
            Assert.Contains("clientCanInfer=true", uploadResponse.Url);
            var status = JsonSerializer.Deserialize<JobStatusDto>(
                await uploadResponse.TextAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(status);
            jobId = status.JobId;

            try
            {
                await Assertions.Expect(page.GetByText("Analysis complete"))
                    .ToBeVisibleAsync(new() { Timeout = FlowTimeoutMs });
            }
            catch (PlaywrightException ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Job did not complete. Browser console:\n{string.Join("\n", console)}\n\n{ex.Message}");
            }
        }
        finally
        {
            File.Delete(wavPath);
        }

        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        var final = await http.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{jobId}");
        Assert.NotNull(final);

        // The plan must still say ClientDelegated after the run — a timeout fall-through would have
        // rewritten it, so this asserts the browser really did the work.
        var pitch = final.Plan.Single(p => p.Stage == "PitchTracking");
        Assert.Equal(ExecutionTier.ClientDelegated, pitch.Tier);
        Assert.Equal("ClientDelegatedPitchTracker", pitch.Executor);

        var notes = await http.GetFromJsonAsync<NoteEvent[]>(
            $"/api/analysis/{jobId}/notes", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(notes);
        Assert.NotEmpty(notes);
        Assert.Contains(notes, n => n.MidiPitch == 69);
    }
}
