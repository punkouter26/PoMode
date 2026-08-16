# PoMode Phase 2: Pipeline Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upload an audio file → background job runs the full 4-stage pipeline against fake executors → live status over SignalR → results (notes/chords JSON) shown in the Blazor UI, restart-safe and cancellable.

**Architecture:** `POST /api/analysis` streams the upload into a per-job folder (`jobs/{id}/`), enqueues the job on a bounded channel, and a single-concurrency `BackgroundService` runs `AnalysisPipeline`: plan stages via `ExecutionPlanner` (tier preference Local → ClientDelegated → Cloud, availability-checked, mid-run fallback), execute each stage through interface seams (`IStemSeparator`/`IPitchTracker`/`IChordRecognizer`/`IModalAnalyzer` — all fakes in this phase), persist `job.json` after every transition (restart-safe), and publish `JobStatusDto` through `IAnalysisNotifier` → SignalR hub. A `HardwareProbe` (NVML GPU + Ollama + cloud keys + Azure detection) feeds `/diag` and, in later phases, the planner.

**Tech Stack:** .NET 10 Minimal APIs, System.Threading.Channels, SignalR (+ `Microsoft.AspNetCore.SignalR.Client` in WASM), NVML P/Invoke, Radzen Blazor, xUnit + `Microsoft.Extensions.TimeProvider.Testing`, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-16-pomode-design.md` (§3 core abstractions, §4 pipeline/routing — this plan implements the Phase-2 slice with fake executors).

**Plan-level rulings (deviations from spec letter, decided now):**
- SignalR pushes ONE event, `JobStatusChanged(JobStatusDto)`, instead of five named events — the DTO carries stage/tier/progress/error, so named events add client switch logic with no information. Spec §4's event list is treated as presentational.
- Intra-stage `StageProgress(pct)` is deferred to Phase 4 (fakes complete instantly; per-stage granularity is enough until real GPU stages exist).
- DXGI adapter enumeration is deferred to Phase 4 (needs COM interop or a package; NVML covers the dev machine, and non-NVIDIA local GPUs report `Gpu: null` until then).
- Enums serialize as numbers (JSON default) — avoids converter wiring across API, SignalR, and WASM clients.
- Job timestamps come from injected `TimeProvider` (testable purge), never `DateTimeOffset.UtcNow` inline.

## Global Constraints

- All Phase 1 constraints hold: `net10.0` + Nullable + TreatWarningsAsErrors from `Directory.Build.props` only; CPM (versions ONLY in `Directory.Packages.props`, added via `dotnet add <proj> package <name>`); `PoMode.` prefixes; NO secrets in appsettings or code; endpoints via `MapGroup()` + `TypedResults`; zero inline CSS (scoped `.razor.css` + `--pm-*` variables); Radzen controls.
- Strict TDD per task: failing test → genuine captured RED output → implement → genuine GREEN output. Fabricated evidence and stray files in commits have both been bounced by review in Phase 1 — stage specific paths only.
- Commits: conventional style (`feat:`/`test:`/`fix:`) ending with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- Stage name constants are the single source: `Separating`, `PitchTracking`, `ChordDetecting`, `ModalAnalysis` (class `StageNames`). `job.json` artifacts: `input.<ext>`, `vocals.wav`, `instrumental.wav`, `notes.json`, `chords.json`, `result.json`.
- Upload limit: 100 MB (`AudioFormatValidator.MaxBytes`), magic-byte validated (RIFF/WAVE; ID3 or 0xFF-sync).
- Run all commands from repo root `c:\Users\punko\Downloads\PoMode`.

---

### Task 1: Shared Analysis & Hardware Contracts

**Files:**
- Create: `src/PoMode.Shared/Analysis/JobContracts.cs`, `src/PoMode.Shared/Analysis/MusicContracts.cs`, `src/PoMode.Shared/Hardware/HardwareReport.cs`
- Modify: `src/PoMode.Shared/Serialization/PoModeJsonContext.cs`, `src/PoMode.Shared/Diagnostics/DiagnosticsReport.cs`
- Test: `tests/PoMode.Unit/Serialization/JsonContextTests.cs` (extend), `tests/PoMode.E2EAPI/DiagnosticsTests.cs` (signature fix only)

**Interfaces:**
- Consumes: Phase 1 `DiagnosticsReport`, `PoModeJsonContext`.
- Produces (every later task consumes these verbatim):
  - `enum JobStage { Uploaded, Separating, PitchTracking, ChordDetecting, ModalAnalysis, Complete, Failed, Cancelled }`
  - `enum ExecutionTier { Local, ClientDelegated, Cloud }`
  - `record StagePlan(string Stage, ExecutionTier Tier, string Executor)`
  - `record JobStatusDto(string JobId, JobStage Stage, double Progress, IReadOnlyList<StagePlan> Plan, IReadOnlyList<string> CompletedStages, string? Error, DateTimeOffset CreatedAt)`
  - `record NoteEvent(int MidiPitch, double StartSec, double DurationSec, int Velocity)`
  - `record ChordSpan(string Symbol, string Root, string Quality, double StartSec, double EndSec)`
  - `record GpuReport(string Vendor, long TotalVramMb, long FreeVramMb, bool CudaAvailable, bool DmlAvailable)`
  - `record HardwareReport(bool IsAzureHosted, GpuReport? Gpu, IReadOnlyList<string> OllamaModels, IReadOnlyList<string> ConfiguredProviders)`
  - `DiagnosticsReport` gains a 6th positional param: `HardwareReport? Hardware`

- [ ] **Step 1: Write the failing tests** — append to `tests/PoMode.Unit/Serialization/JsonContextTests.cs`:

```csharp
[Fact]
public void JobStatusDto_round_trips_via_source_gen_context()
{
    var dto = new JobStatusDto(
        JobId: "abc123",
        Stage: JobStage.PitchTracking,
        Progress: 0.25,
        Plan: [new StagePlan("Separating", ExecutionTier.Local, "FakeStemSeparator")],
        CompletedStages: ["Separating"],
        Error: null,
        CreatedAt: DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

    var json = JsonSerializer.Serialize(dto, PoModeJsonContext.Default.JobStatusDto);
    var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.JobStatusDto);

    Assert.NotNull(back);
    Assert.Equal(JobStage.PitchTracking, back.Stage);
    Assert.Equal("FakeStemSeparator", back.Plan[0].Executor);
    Assert.Equal(["Separating"], back.CompletedStages);
}

[Fact]
public void NoteEvents_and_ChordSpans_round_trip_as_lists()
{
    List<NoteEvent> notes = [new(60, 0.0, 0.45, 96)];
    List<ChordSpan> chords = [new("Am7", "A", "min7", 0.0, 2.0)];

    var notesBack = JsonSerializer.Deserialize(
        JsonSerializer.Serialize(notes, PoModeJsonContext.Default.ListNoteEvent),
        PoModeJsonContext.Default.ListNoteEvent);
    var chordsBack = JsonSerializer.Deserialize(
        JsonSerializer.Serialize(chords, PoModeJsonContext.Default.ListChordSpan),
        PoModeJsonContext.Default.ListChordSpan);

    Assert.Equal(60, notesBack![0].MidiPitch);
    Assert.Equal("Am7", chordsBack![0].Symbol);
}

[Fact]
public void DiagnosticsReport_carries_optional_hardware_report()
{
    var report = new DiagnosticsReport(
        EnvironmentName: "Development",
        IsAzureHosted: false,
        SecretSource: "EnvironmentVariables",
        SecretFellBack: false,
        ProviderKeys: [],
        Hardware: new HardwareReport(
            IsAzureHosted: false,
            Gpu: new GpuReport("NVIDIA", 8192, 6000, CudaAvailable: true, DmlAvailable: true),
            OllamaModels: ["qwen2.5:7b"],
            ConfiguredProviders: ["ReplicateApiToken"]));

    var back = JsonSerializer.Deserialize(
        JsonSerializer.Serialize(report, PoModeJsonContext.Default.DiagnosticsReport),
        PoModeJsonContext.Default.DiagnosticsReport);

    Assert.NotNull(back?.Hardware?.Gpu);
    Assert.Equal(6000, back.Hardware.Gpu.FreeVramMb);
    Assert.Equal(["qwen2.5:7b"], back.Hardware.OllamaModels);
}
```

Add `using PoMode.Shared.Analysis;` and `using PoMode.Shared.Hardware;` to the file's usings.

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/PoMode.Unit --filter JsonContextTests`
Expected: FAIL — compile errors (`JobStatusDto` etc. don't exist).

- [ ] **Step 3: Implement**

`src/PoMode.Shared/Analysis/JobContracts.cs`:
```csharp
namespace PoMode.Shared.Analysis;

public enum JobStage
{
    Uploaded,
    Separating,
    PitchTracking,
    ChordDetecting,
    ModalAnalysis,
    Complete,
    Failed,
    Cancelled,
}

public enum ExecutionTier
{
    Local,
    ClientDelegated,
    Cloud,
}

public sealed record StagePlan(string Stage, ExecutionTier Tier, string Executor);

public sealed record JobStatusDto(
    string JobId,
    JobStage Stage,
    double Progress,
    IReadOnlyList<StagePlan> Plan,
    IReadOnlyList<string> CompletedStages,
    string? Error,
    DateTimeOffset CreatedAt);
```

`src/PoMode.Shared/Analysis/MusicContracts.cs`:
```csharp
namespace PoMode.Shared.Analysis;

public sealed record NoteEvent(int MidiPitch, double StartSec, double DurationSec, int Velocity);

public sealed record ChordSpan(string Symbol, string Root, string Quality, double StartSec, double EndSec);
```

`src/PoMode.Shared/Hardware/HardwareReport.cs`:
```csharp
namespace PoMode.Shared.Hardware;

public sealed record GpuReport(
    string Vendor,
    long TotalVramMb,
    long FreeVramMb,
    bool CudaAvailable,
    bool DmlAvailable);

public sealed record HardwareReport(
    bool IsAzureHosted,
    GpuReport? Gpu,
    IReadOnlyList<string> OllamaModels,
    IReadOnlyList<string> ConfiguredProviders);
```

`src/PoMode.Shared/Diagnostics/DiagnosticsReport.cs` — extend the record (and keep `ProviderKeyStatus` unchanged):
```csharp
using PoMode.Shared.Hardware;

namespace PoMode.Shared.Diagnostics;

public sealed record DiagnosticsReport(
    string EnvironmentName,
    bool IsAzureHosted,
    string SecretSource,
    bool SecretFellBack,
    IReadOnlyList<ProviderKeyStatus> ProviderKeys,
    HardwareReport? Hardware);

public sealed record ProviderKeyStatus(string Provider, bool Configured);
```

`src/PoMode.Shared/Serialization/PoModeJsonContext.cs` — replace attribute list:
```csharp
using System.Text.Json.Serialization;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Hardware;
using PoMode.Shared.Session;

namespace PoMode.Shared.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiagnosticsReport))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(JobStatusDto))]
[JsonSerializable(typeof(HardwareReport))]
[JsonSerializable(typeof(List<NoteEvent>))]
[JsonSerializable(typeof(List<ChordSpan>))]
public sealed partial class PoModeJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Fix the two existing construction sites broken by the new `Hardware` param**

`src/PoMode.API/Features/Hardware/DiagnosticsService.cs` — add `Hardware: null` as the final argument of the `DiagnosticsReport` constructor call (Task 6 replaces this with the real probe).
`tests/PoMode.Unit/Serialization/JsonContextTests.cs` — the ORIGINAL `DiagnosticsReport_round_trips_via_source_gen_context` test: add `Hardware: null` to its constructor call.

- [ ] **Step 5: Run to verify GREEN**

Run: `dotnet test`
Expected: all pass (18 existing + 3 new = 21), zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/PoMode.Shared src/PoMode.API/Features/Hardware/DiagnosticsService.cs tests/PoMode.Unit/Serialization/JsonContextTests.cs
git commit -m "feat: shared job, music, and hardware contracts for the analysis pipeline"
```

---

### Task 2: Test Audio Helper & Upload Format Validator

**Files:**
- Create: `tests/TestCommon/TestAudio.cs`, `src/PoMode.API/Features/Analysis/AudioFormatValidator.cs`
- Modify: `tests/PoMode.Unit/PoMode.Unit.csproj`, `tests/PoMode.Integration/PoMode.Integration.csproj`, `tests/PoMode.E2EAPI/PoMode.E2EAPI.csproj`, `tests/PoMode.E2EUI/PoMode.E2EUI.csproj` (linked-file include)
- Test: `tests/PoMode.Unit/Analysis/AudioFormatValidatorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `PoMode.TestCommon.TestAudio.MakeWav(double seconds = 0.1, int sampleRate = 8000)` → `byte[]` (valid RIFF/WAVE PCM16 mono silence) — linked into all four test projects
  - `AudioFormatValidator.MaxBytes` (`100L * 1024 * 1024`)
  - `static bool AudioFormatValidator.IsSupported(ReadOnlySpan<byte> header, out string format)` — `format` ∈ `"wav"`/`"mp3"`/`""`

- [ ] **Step 1: Write the shared test helper** — `tests/TestCommon/TestAudio.cs`:

```csharp
namespace PoMode.TestCommon;

/// <summary>Generates minimal valid audio fixtures for tests. Linked (not referenced) into each test project.</summary>
public static class TestAudio
{
    public static byte[] MakeWav(double seconds = 0.1, int sampleRate = 8000)
    {
        var samples = (int)(seconds * sampleRate);
        var dataSize = samples * 2; // PCM16 mono
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);          // PCM
        writer.Write((short)1);          // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);    // byte rate
        writer.Write((short)2);          // block align
        writer.Write((short)16);         // bits per sample
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]); // silence
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] MakeId3Mp3Header() => [.. "ID3"u8, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    public static byte[] MakeFrameSyncMp3Header() => [0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
}
```

Add to EACH of the four test csproj files (inside a new `<ItemGroup>`):
```xml
<Compile Include="..\TestCommon\TestAudio.cs" Link="TestAudio.cs" />
```

- [ ] **Step 2: Write the failing tests** — `tests/PoMode.Unit/Analysis/AudioFormatValidatorTests.cs`:

```csharp
using PoMode.API.Features.Analysis;
using PoMode.TestCommon;

namespace PoMode.Unit.Analysis;

public class AudioFormatValidatorTests
{
    [Fact]
    public void Wav_header_is_supported()
    {
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeWav(), out var format));
        Assert.Equal("wav", format);
    }

    [Fact]
    public void Id3_mp3_header_is_supported()
    {
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeId3Mp3Header(), out var format));
        Assert.Equal("mp3", format);
    }

    [Fact]
    public void Frame_sync_mp3_header_is_supported()
    {
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeFrameSyncMp3Header(), out var format));
        Assert.Equal("mp3", format);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A, 0x25, 0x0A, 0x0A })] // %PDF-1.4
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20 })] // RIFF....AVI (not WAVE)
    public void Other_content_is_rejected(byte[] header)
    {
        Assert.False(AudioFormatValidator.IsSupported(header, out var format));
        Assert.Equal("", format);
    }

    [Fact]
    public void Max_size_is_100_mb()
        => Assert.Equal(104_857_600L, AudioFormatValidator.MaxBytes);
}
```

- [ ] **Step 3: Run to verify RED**

Run: `dotnet test tests/PoMode.Unit --filter AudioFormatValidatorTests`
Expected: FAIL — compile error, `AudioFormatValidator` does not exist.

- [ ] **Step 4: Implement** — `src/PoMode.API/Features/Analysis/AudioFormatValidator.cs`:

```csharp
namespace PoMode.API.Features.Analysis;

/// <summary>Magic-byte sniffing for uploads; extension is never trusted.</summary>
public static class AudioFormatValidator
{
    public const long MaxBytes = 100L * 1024 * 1024;

    public static bool IsSupported(ReadOnlySpan<byte> header, out string format)
    {
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
        {
            format = "wav";
            return true;
        }

        if (header.Length >= 3 && header[..3].SequenceEqual("ID3"u8))
        {
            format = "mp3";
            return true;
        }

        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
        {
            format = "mp3";
            return true;
        }

        format = "";
        return false;
    }
}
```

- [ ] **Step 5: Run to verify GREEN**

Run: `dotnet test tests/PoMode.Unit --filter AudioFormatValidatorTests` then full `dotnet test`
Expected: 8 new pass; all green, zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add tests/TestCommon src/PoMode.API/Features/Analysis/AudioFormatValidator.cs tests/PoMode.Unit/Analysis tests/PoMode.Unit/PoMode.Unit.csproj tests/PoMode.Integration/PoMode.Integration.csproj tests/PoMode.E2EAPI/PoMode.E2EAPI.csproj tests/PoMode.E2EUI/PoMode.E2EUI.csproj
git commit -m "feat: magic-byte audio format validator and shared wav test fixture helper"
```

---

### Task 3: JobState & JobStore (Persistence + Purge)

**Files:**
- Create: `src/PoMode.API/Features/Analysis/JobState.cs`, `src/PoMode.API/Features/Analysis/JobStore.cs`
- Test: `tests/PoMode.Integration/JobStoreTests.cs`

**Interfaces:**
- Consumes: `JobStage`/`StagePlan`/`JobStatusDto` (Task 1), `TestAudio` (Task 2), config key `Jobs:RootPath` (Phase 1).
- Produces:
  - `JobState` — mutable class: `required string JobId`, `required string InputFileName`, `required DateTimeOffset CreatedAt`, `JobStage Stage` (=Uploaded), `double Progress`, `List<StagePlan> Plan`, `List<string> CompletedStages`, `string? Error`; method `JobStatusDto ToDto()`
  - `JobStore(IConfiguration, TimeProvider)` — `string RootPath`, `string JobDir(string jobId)`, `string InputPath(JobState)`, `Task<JobState> CreateAsync(string fileName, Stream content, CancellationToken)`, `Task SaveAsync(JobState, CancellationToken)`, `Task<JobState?> LoadAsync(string jobId, CancellationToken)`, `int PurgeOlderThan(TimeSpan maxAge)`
  - Register `TimeProvider.System` in DI (done in Task 8).

- [ ] **Step 1: Add the fake-clock test package**

```powershell
dotnet add tests/PoMode.Integration package Microsoft.Extensions.TimeProvider.Testing
```

- [ ] **Step 2: Write the failing tests** — `tests/PoMode.Integration/JobStoreTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;

namespace PoMode.Integration;

public sealed class JobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-store-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

    private JobStore Store => new(
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
        _clock);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Create_writes_input_file_and_state_json()
    {
        var store = Store;
        using var content = new MemoryStream(TestAudio.MakeWav());

        var state = await store.CreateAsync("song.wav", content, CancellationToken.None);

        Assert.True(File.Exists(store.InputPath(state)));
        Assert.True(File.Exists(Path.Combine(store.JobDir(state.JobId), "job.json")));
        Assert.Equal(JobStage.Uploaded, state.Stage);
        Assert.Equal(_clock.GetUtcNow(), state.CreatedAt);
    }

    [Fact]
    public async Task Save_then_Load_round_trips_all_mutable_fields()
    {
        var store = Store;
        using var content = new MemoryStream(TestAudio.MakeWav());
        var state = await store.CreateAsync("song.wav", content, CancellationToken.None);

        state.Stage = JobStage.ChordDetecting;
        state.Progress = 0.5;
        state.Plan = [new StagePlan("Separating", ExecutionTier.Local, "FakeStemSeparator")];
        state.CompletedStages = ["Separating", "PitchTracking"];
        state.Error = null;
        await store.SaveAsync(state, CancellationToken.None);

        var loaded = await store.LoadAsync(state.JobId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(JobStage.ChordDetecting, loaded.Stage);
        Assert.Equal(0.5, loaded.Progress);
        Assert.Equal("FakeStemSeparator", loaded.Plan[0].Executor);
        Assert.Equal(["Separating", "PitchTracking"], loaded.CompletedStages);
    }

    [Fact]
    public async Task Load_of_unknown_job_returns_null()
        => Assert.Null(await Store.LoadAsync("does-not-exist", CancellationToken.None));

    [Fact]
    public async Task Purge_removes_only_jobs_older_than_max_age()
    {
        var store = Store;
        using var old = new MemoryStream(TestAudio.MakeWav());
        var oldJob = await store.CreateAsync("old.wav", old, CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(8));
        using var fresh = new MemoryStream(TestAudio.MakeWav());
        var freshJob = await store.CreateAsync("fresh.wav", fresh, CancellationToken.None);

        var purged = store.PurgeOlderThan(TimeSpan.FromDays(7));

        Assert.Equal(1, purged);
        Assert.False(Directory.Exists(store.JobDir(oldJob.JobId)));
        Assert.True(Directory.Exists(store.JobDir(freshJob.JobId)));
    }

    [Fact]
    public void ToDto_maps_every_field()
    {
        var state = new JobState
        {
            JobId = "j1",
            InputFileName = "a.wav",
            CreatedAt = _clock.GetUtcNow(),
            Stage = JobStage.Failed,
            Progress = 0.75,
            Plan = [new StagePlan("Separating", ExecutionTier.Cloud, "Replicate")],
            CompletedStages = ["Separating"],
            Error = "boom",
        };

        var dto = state.ToDto();

        Assert.Equal(("j1", JobStage.Failed, 0.75, "boom"), (dto.JobId, dto.Stage, dto.Progress, dto.Error));
        Assert.Equal(ExecutionTier.Cloud, dto.Plan[0].Tier);
        Assert.Equal(_clock.GetUtcNow(), dto.CreatedAt);
    }
}
```

- [ ] **Step 3: Run to verify RED**

Run: `dotnet test tests/PoMode.Integration --filter JobStoreTests`
Expected: FAIL — compile errors (`JobState`, `JobStore` missing).

- [ ] **Step 4: Implement**

`src/PoMode.API/Features/Analysis/JobState.cs`:
```csharp
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Persisted per-job state (jobs/{id}/job.json). Mutable: the pipeline updates it as stages run.</summary>
public sealed class JobState
{
    public required string JobId { get; init; }
    public required string InputFileName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public JobStage Stage { get; set; } = JobStage.Uploaded;
    public double Progress { get; set; }
    public List<StagePlan> Plan { get; set; } = [];
    public List<string> CompletedStages { get; set; } = [];
    public string? Error { get; set; }

    public JobStatusDto ToDto() => new(JobId, Stage, Progress, Plan, CompletedStages, Error, CreatedAt);
}
```

`src/PoMode.API/Features/Analysis/JobStore.cs`:
```csharp
using System.Text.Json;

namespace PoMode.API.Features.Analysis;

/// <summary>Per-job folder persistence under Jobs:RootPath. The folder is the source of truth (no database).</summary>
public sealed class JobStore(IConfiguration configuration, TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string RootPath
    {
        get
        {
            var configured = configuration["Jobs:RootPath"];
            return string.IsNullOrEmpty(configured)
                ? Path.Combine(AppContext.BaseDirectory, "jobs")
                : configured;
        }
    }

    public string JobDir(string jobId) => Path.Combine(RootPath, jobId);

    public string InputPath(JobState state)
        => Path.Combine(JobDir(state.JobId), "input" + Path.GetExtension(state.InputFileName));

    private string StatePath(string jobId) => Path.Combine(JobDir(jobId), "job.json");

    public async Task<JobState> CreateAsync(string fileName, Stream content, CancellationToken ct)
    {
        var state = new JobState
        {
            JobId = Guid.NewGuid().ToString("N"),
            InputFileName = fileName,
            CreatedAt = time.GetUtcNow(),
        };
        Directory.CreateDirectory(JobDir(state.JobId));
        await using (var file = File.Create(InputPath(state)))
        {
            await content.CopyToAsync(file, ct);
        }
        await SaveAsync(state, ct);
        return state;
    }

    public async Task SaveAsync(JobState state, CancellationToken ct)
        => await File.WriteAllTextAsync(StatePath(state.JobId), JsonSerializer.Serialize(state, JsonOptions), ct);

    public async Task<JobState?> LoadAsync(string jobId, CancellationToken ct)
    {
        var path = StatePath(jobId);
        if (!File.Exists(path))
        {
            return null;
        }
        return JsonSerializer.Deserialize<JobState>(await File.ReadAllTextAsync(path, ct), JsonOptions);
    }

    public int PurgeOlderThan(TimeSpan maxAge)
    {
        if (!Directory.Exists(RootPath))
        {
            return 0;
        }

        var cutoff = time.GetUtcNow() - maxAge;
        var purged = 0;
        foreach (var dir in Directory.GetDirectories(RootPath))
        {
            var statePath = Path.Combine(dir, "job.json");
            DateTimeOffset createdAt;
            try
            {
                createdAt = File.Exists(statePath)
                    ? JsonSerializer.Deserialize<JobState>(File.ReadAllText(statePath), JsonOptions)?.CreatedAt
                      ?? File.GetLastWriteTimeUtc(dir)
                    : File.GetLastWriteTimeUtc(dir);
            }
            catch (JsonException)
            {
                createdAt = File.GetLastWriteTimeUtc(dir);
            }

            if (createdAt < cutoff)
            {
                Directory.Delete(dir, recursive: true);
                purged++;
            }
        }
        return purged;
    }
}
```

- [ ] **Step 5: Run to verify GREEN**

Run: `dotnet test tests/PoMode.Integration --filter JobStoreTests` then full `dotnet test`
Expected: 5 new pass; all green, zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/PoMode.API/Features/Analysis tests/PoMode.Integration
git commit -m "feat: job state persistence with per-job folders and time-provider purge"
```

---

### Task 4: Stage Contracts & Fake Executors

**Files:**
- Create: `src/PoMode.API/Pipeline/StageContracts.cs`, `src/PoMode.API/Features/StemSeparation/FakeStemSeparator.cs`, `src/PoMode.API/Features/PitchTracking/FakePitchTracker.cs`, `src/PoMode.API/Features/ChordRecognition/FakeChordRecognizer.cs`, `src/PoMode.API/Features/ModalAnalysis/PlaceholderModalAnalyzer.cs`
- Test: `tests/PoMode.Unit/Pipeline/FakeExecutorTests.cs`

**Interfaces:**
- Consumes: `NoteEvent`/`ChordSpan`/`ExecutionTier` (Task 1).
- Produces (pipeline + planner consume; Phase 4/5 add real implementations behind the same interfaces):
  - `record StageContext(string JobId, string JobDir, string InputPath)`
  - `static class StageNames { Separating, PitchTracking, ChordDetecting, ModalAnalysis }` (const strings, values = names)
  - `interface IStageExecutor { string Name { get; } ExecutionTier Tier { get; } Task<bool> IsAvailableAsync(CancellationToken ct); }`
  - `interface IStemSeparator : IStageExecutor { Task SeparateAsync(StageContext context, CancellationToken ct); }` — writes `vocals.wav` + `instrumental.wav` into `context.JobDir`
  - `interface IPitchTracker : IStageExecutor { Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct); }`
  - `interface IChordRecognizer : IStageExecutor { Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct); }`
  - `interface IModalAnalyzer { Task AnalyzeAsync(StageContext context, CancellationToken ct); }` — writes `result.json`
  - Fakes: `FakeStemSeparator` (copies input to both stems), `FakePitchTracker` (C-major scale: pitches 60,62,64,65,67,69,71,72 at `i*0.5s`, duration 0.45, velocity 96), `FakeChordRecognizer` (C/Am/F/G, 2s each), `PlaceholderModalAnalyzer` (writes `{"status":"modal analysis arrives in Phase 3"}`). All: `Name = type name`, `Tier = Local`, always available.

- [ ] **Step 1: Write the failing tests** — `tests/PoMode.Unit/Pipeline/FakeExecutorTests.cs`:

```csharp
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.StemSeparation;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;

namespace PoMode.Unit.Pipeline;

public sealed class FakeExecutorTests : IDisposable
{
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), $"pomode-fake-{Guid.NewGuid():N}");

    public FakeExecutorTests() => Directory.CreateDirectory(_jobDir);

    public void Dispose() => Directory.Delete(_jobDir, recursive: true);

    private StageContext Context()
    {
        var input = Path.Combine(_jobDir, "input.wav");
        File.WriteAllBytes(input, TestAudio.MakeWav());
        return new StageContext("job1", _jobDir, input);
    }

    [Fact]
    public async Task FakeStemSeparator_writes_both_stems()
    {
        var context = Context();
        await new FakeStemSeparator().SeparateAsync(context, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_jobDir, "vocals.wav")));
        Assert.True(File.Exists(Path.Combine(_jobDir, "instrumental.wav")));
    }

    [Fact]
    public async Task FakePitchTracker_returns_deterministic_c_major_scale()
    {
        var notes = await new FakePitchTracker().TrackAsync(Context(), CancellationToken.None);

        Assert.Equal(8, notes.Count);
        Assert.Equal(60, notes[0].MidiPitch);
        Assert.Equal(72, notes[7].MidiPitch);
        Assert.Equal(3.5, notes[7].StartSec);
        Assert.All(notes, n => Assert.Equal(96, n.Velocity));
    }

    [Fact]
    public async Task FakeChordRecognizer_returns_four_two_second_chords()
    {
        var chords = await new FakeChordRecognizer().RecognizeAsync(Context(), CancellationToken.None);

        Assert.Equal(4, chords.Count);
        Assert.Equal(["C", "Am", "F", "G"], chords.Select(c => c.Symbol).ToArray());
        Assert.All(chords, c => Assert.Equal(2.0, c.EndSec - c.StartSec));
        Assert.Equal(8.0, chords[^1].EndSec);
    }

    [Fact]
    public async Task PlaceholderModalAnalyzer_writes_result_json()
    {
        await new PlaceholderModalAnalyzer().AnalyzeAsync(Context(), CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(_jobDir, "result.json"));
        Assert.Contains("Phase 3", text);
    }

    [Fact]
    public async Task All_fakes_are_local_tier_and_available()
    {
        IStageExecutor[] executors = [new FakeStemSeparator(), new FakePitchTracker(), new FakeChordRecognizer()];
        foreach (var executor in executors)
        {
            Assert.Equal(ExecutionTier.Local, executor.Tier);
            Assert.True(await executor.IsAvailableAsync(CancellationToken.None));
        }
    }
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/PoMode.Unit --filter FakeExecutorTests`
Expected: FAIL — compile errors.

- [ ] **Step 3: Implement**

`src/PoMode.API/Pipeline/StageContracts.cs`:
```csharp
using PoMode.Shared.Analysis;

namespace PoMode.API.Pipeline;

public sealed record StageContext(string JobId, string JobDir, string InputPath);

public static class StageNames
{
    public const string Separating = "Separating";
    public const string PitchTracking = "PitchTracking";
    public const string ChordDetecting = "ChordDetecting";
    public const string ModalAnalysis = "ModalAnalysis";
}

public interface IStageExecutor
{
    string Name { get; }
    ExecutionTier Tier { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

public interface IStemSeparator : IStageExecutor
{
    /// <summary>Writes vocals.wav and instrumental.wav into <see cref="StageContext.JobDir"/>.</summary>
    Task SeparateAsync(StageContext context, CancellationToken ct);
}

public interface IPitchTracker : IStageExecutor
{
    Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct);
}

public interface IChordRecognizer : IStageExecutor
{
    Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct);
}

public interface IModalAnalyzer
{
    /// <summary>Writes result.json into <see cref="StageContext.JobDir"/>.</summary>
    Task AnalyzeAsync(StageContext context, CancellationToken ct);
}
```

`src/PoMode.API/Features/StemSeparation/FakeStemSeparator.cs`:
```csharp
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.StemSeparation;

/// <summary>Phase-2 stand-in: copies the input as both stems so downstream stages have real files.</summary>
public sealed class FakeStemSeparator : IStemSeparator
{
    public string Name => nameof(FakeStemSeparator);
    public ExecutionTier Tier => ExecutionTier.Local;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task SeparateAsync(StageContext context, CancellationToken ct)
    {
        File.Copy(context.InputPath, Path.Combine(context.JobDir, "vocals.wav"), overwrite: true);
        File.Copy(context.InputPath, Path.Combine(context.JobDir, "instrumental.wav"), overwrite: true);
        return Task.CompletedTask;
    }
}
```

`src/PoMode.API/Features/PitchTracking/FakePitchTracker.cs`:
```csharp
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>Phase-2 stand-in: deterministic C-major scale regardless of input audio.</summary>
public sealed class FakePitchTracker : IPitchTracker
{
    private static readonly int[] Pitches = [60, 62, 64, 65, 67, 69, 71, 72];

    public string Name => nameof(FakePitchTracker);
    public ExecutionTier Tier => ExecutionTier.Local;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
    {
        IReadOnlyList<NoteEvent> notes = Pitches
            .Select((pitch, i) => new NoteEvent(pitch, i * 0.5, 0.45, 96))
            .ToArray();
        return Task.FromResult(notes);
    }
}
```

`src/PoMode.API/Features/ChordRecognition/FakeChordRecognizer.cs`:
```csharp
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>Phase-2 stand-in: a fixed C / Am / F / G progression, two seconds per chord.</summary>
public sealed class FakeChordRecognizer : IChordRecognizer
{
    private static readonly (string Symbol, string Root, string Quality)[] Progression =
        [("C", "C", "maj"), ("Am", "A", "min"), ("F", "F", "maj"), ("G", "G", "maj")];

    public string Name => nameof(FakeChordRecognizer);
    public ExecutionTier Tier => ExecutionTier.Local;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct)
    {
        IReadOnlyList<ChordSpan> chords = Progression
            .Select((chord, i) => new ChordSpan(chord.Symbol, chord.Root, chord.Quality, i * 2.0, (i + 1) * 2.0))
            .ToArray();
        return Task.FromResult(chords);
    }
}
```

`src/PoMode.API/Features/ModalAnalysis/PlaceholderModalAnalyzer.cs`:
```csharp
using PoMode.API.Pipeline;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Stage-4 placeholder until the real ModalAnalysisEngine lands in Phase 3.</summary>
public sealed class PlaceholderModalAnalyzer : IModalAnalyzer
{
    public Task AnalyzeAsync(StageContext context, CancellationToken ct)
        => File.WriteAllTextAsync(
            Path.Combine(context.JobDir, "result.json"),
            """{"status":"modal analysis arrives in Phase 3"}""",
            ct);
}
```

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test tests/PoMode.Unit --filter FakeExecutorTests` then full `dotnet test`
Expected: 5 new pass; all green, zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.API/Pipeline src/PoMode.API/Features tests/PoMode.Unit/Pipeline
git commit -m "feat: pipeline stage contracts with deterministic fake executors"
```

---

### Task 5: ExecutionPlanner

**Files:**
- Create: `src/PoMode.API/Pipeline/ExecutionPlanner.cs`
- Test: `tests/PoMode.Unit/Pipeline/ExecutionPlannerTests.cs`

**Interfaces:**
- Consumes: stage contracts (Task 4), `StagePlan`/`ExecutionTier` (Task 1).
- Produces:
  - `ExecutionPlanner(IEnumerable<IStemSeparator>, IEnumerable<IPitchTracker>, IEnumerable<IChordRecognizer>)`
  - `static int ExecutionPlanner.TierRank(ExecutionTier)` — Local 0, ClientDelegated 1, Cloud 2
  - `Task<List<StagePlan>> PlanAsync(CancellationToken)` — 4 entries in stage order; per stage: candidates ordered by TierRank, first whose `IsAvailableAsync` is true wins; ModalAnalysis is always `(Local, "ModalAnalysisEngine")`; no candidate available → `InvalidOperationException` naming the stage.

- [ ] **Step 1: Write the failing tests** — `tests/PoMode.Unit/Pipeline/ExecutionPlannerTests.cs`:

```csharp
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.Unit.Pipeline;

public class ExecutionPlannerTests
{
    private sealed class StubExecutor(string name, ExecutionTier tier, bool available)
        : IStemSeparator, IPitchTracker, IChordRecognizer
    {
        public string Name => name;
        public ExecutionTier Tier => tier;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(available);
        public Task SeparateAsync(StageContext context, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<NoteEvent>>([]);
        public Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChordSpan>>([]);
    }

    private static ExecutionPlanner Planner(params StubExecutor[] executors)
        => new(executors, executors, executors);

    [Fact]
    public async Task Prefers_local_over_client_delegated_over_cloud()
    {
        var planner = Planner(
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true),
            new StubExecutor("BrowserX", ExecutionTier.ClientDelegated, available: true),
            new StubExecutor("LocalX", ExecutionTier.Local, available: true));

        var plan = await planner.PlanAsync(CancellationToken.None);

        Assert.Equal("LocalX", plan[0].Executor);
        Assert.Equal(ExecutionTier.Local, plan[0].Tier);
    }

    [Fact]
    public async Task Skips_unavailable_executors()
    {
        var planner = Planner(
            new StubExecutor("LocalX", ExecutionTier.Local, available: false),
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true));

        var plan = await planner.PlanAsync(CancellationToken.None);

        Assert.Equal("CloudX", plan[0].Executor);
    }

    [Fact]
    public async Task Produces_all_four_stages_in_order_with_fixed_modal_stage()
    {
        var planner = Planner(new StubExecutor("LocalX", ExecutionTier.Local, available: true));

        var plan = await planner.PlanAsync(CancellationToken.None);

        Assert.Equal(
            [StageNames.Separating, StageNames.PitchTracking, StageNames.ChordDetecting, StageNames.ModalAnalysis],
            plan.Select(p => p.Stage).ToArray());
        Assert.Equal("ModalAnalysisEngine", plan[3].Executor);
        Assert.Equal(ExecutionTier.Local, plan[3].Tier);
    }

    [Fact]
    public async Task Throws_naming_the_stage_when_nothing_is_available()
    {
        var planner = Planner(new StubExecutor("LocalX", ExecutionTier.Local, available: false));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planner.PlanAsync(CancellationToken.None));

        Assert.Contains(StageNames.Separating, exception.Message);
    }

    [Fact]
    public void Tier_rank_orders_local_first_cloud_last()
    {
        Assert.True(ExecutionPlanner.TierRank(ExecutionTier.Local)
            < ExecutionPlanner.TierRank(ExecutionTier.ClientDelegated));
        Assert.True(ExecutionPlanner.TierRank(ExecutionTier.ClientDelegated)
            < ExecutionPlanner.TierRank(ExecutionTier.Cloud));
    }
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/PoMode.Unit --filter ExecutionPlannerTests`
Expected: FAIL — compile error, `ExecutionPlanner` missing.

- [ ] **Step 3: Implement** — `src/PoMode.API/Pipeline/ExecutionPlanner.cs`:

```csharp
using PoMode.Shared.Analysis;

namespace PoMode.API.Pipeline;

/// <summary>Resolves each stage to the best available executor: Local → ClientDelegated → Cloud (paid last).</summary>
public sealed class ExecutionPlanner(
    IEnumerable<IStemSeparator> stemSeparators,
    IEnumerable<IPitchTracker> pitchTrackers,
    IEnumerable<IChordRecognizer> chordRecognizers)
{
    public static int TierRank(ExecutionTier tier) => tier switch
    {
        ExecutionTier.Local => 0,
        ExecutionTier.ClientDelegated => 1,
        ExecutionTier.Cloud => 2,
        _ => int.MaxValue,
    };

    public async Task<List<StagePlan>> PlanAsync(CancellationToken ct) =>
    [
        await PlanStageAsync(StageNames.Separating, stemSeparators, ct),
        await PlanStageAsync(StageNames.PitchTracking, pitchTrackers, ct),
        await PlanStageAsync(StageNames.ChordDetecting, chordRecognizers, ct),
        new StagePlan(StageNames.ModalAnalysis, ExecutionTier.Local, "ModalAnalysisEngine"),
    ];

    private static async Task<StagePlan> PlanStageAsync<TExecutor>(
        string stage, IEnumerable<TExecutor> candidates, CancellationToken ct)
        where TExecutor : IStageExecutor
    {
        foreach (var candidate in candidates.OrderBy(c => TierRank(c.Tier)))
        {
            if (await candidate.IsAvailableAsync(ct))
            {
                return new StagePlan(stage, candidate.Tier, candidate.Name);
            }
        }
        throw new InvalidOperationException($"No executor is available for stage {stage}.");
    }
}
```

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test tests/PoMode.Unit --filter ExecutionPlannerTests` then full `dotnet test`
Expected: 5 new pass; all green, zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.API/Pipeline tests/PoMode.Unit/Pipeline
git commit -m "feat: tier-preferring execution planner with availability checks"
```

---

### Task 6: HardwareProbe (NVML + Ollama) & /diag Extension

**Files:**
- Create: `src/PoMode.API/Features/Hardware/NvmlInterop.cs`, `src/PoMode.API/Features/Hardware/HardwareProbe.cs`, `src/PoMode.API/Infrastructure/EnvironmentDetector.cs`
- Modify: `src/PoMode.API/Features/Hardware/DiagnosticsService.cs`, `src/PoMode.API/Features/Hardware/DiagnosticsEndpoints.cs`, `src/PoMode.API/Program.cs` (add `AddHttpClient()` + `HardwareProbe` singleton)
- Test: `tests/PoMode.Integration/HardwareProbeTests.cs`, `tests/PoMode.E2EAPI/DiagnosticsTests.cs` (extend)

**Interfaces:**
- Consumes: `HardwareReport`/`GpuReport` (Task 1).
- Produces:
  - `static bool EnvironmentDetector.IsAzureHosted()` — `WEBSITE_INSTANCE_ID` present or `DOTNET_RUNNING_IN_CONTAINER == "true"` (extracted from DiagnosticsService)
  - `static GpuReport? NvmlInterop.TryProbe()` — NVML P/Invoke; `null` when nvml.dll absent or any call fails; `CudaAvailable = true` on success; `DmlAvailable = OperatingSystem.IsWindows()`
  - `HardwareProbe(IConfiguration, IHttpClientFactory)` — `Task<HardwareReport> ProbeAsync(CancellationToken)`; Azure mode skips GPU + Ollama probes; Ollama = `GET http://localhost:11434/api/tags`, 1s timeout, parse `models[].name`, failures → empty list
  - `DiagnosticsService.BuildReportAsync(CancellationToken)` (async now) — fills `Hardware`

- [ ] **Step 1: Write the failing integration tests** — `tests/PoMode.Integration/HardwareProbeTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoMode.API.Features.Hardware;
using PoMode.API.Infrastructure;

namespace PoMode.Integration;

public class HardwareProbeTests
{
    private static HardwareProbe Probe(Dictionary<string, string?>? config = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        return new HardwareProbe(
            new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build(),
            provider.GetRequiredService<IHttpClientFactory>());
    }

    [Fact]
    public void Nvml_probe_never_throws_and_reports_nvidia_when_present()
    {
        var gpu = NvmlInterop.TryProbe(); // must not throw on machines without nvml.dll
        if (gpu is not null)
        {
            Assert.Equal("NVIDIA", gpu.Vendor);
            Assert.True(gpu.TotalVramMb > 0);
            Assert.True(gpu.FreeVramMb <= gpu.TotalVramMb);
            Assert.True(gpu.CudaAvailable);
        }
    }

    [Fact]
    public async Task Probe_reports_configured_providers_from_config()
    {
        var probe = Probe(new() { ["ReplicateApiToken"] = "x", ["SonicApiKey"] = "" });

        var report = await probe.ProbeAsync(CancellationToken.None);

        Assert.Contains("ReplicateApiToken", report.ConfiguredProviders);
        Assert.DoesNotContain("SonicApiKey", report.ConfiguredProviders);
        Assert.DoesNotContain("LalalApiKey", report.ConfiguredProviders);
    }

    [Fact]
    public async Task Probe_never_throws_when_ollama_is_unreachable()
    {
        // Whatever the machine state, ProbeAsync must complete and OllamaModels must be non-null.
        var report = await Probe().ProbeAsync(CancellationToken.None);

        Assert.NotNull(report.OllamaModels);
        Assert.False(report.IsAzureHosted);
    }
}
```

- [ ] **Step 2: Extend the E2EAPI diag test** — in `tests/PoMode.E2EAPI/DiagnosticsTests.cs`, add inside `Diag_reports_provider_key_presence_without_leaking_values` after the existing asserts:

```csharp
Assert.NotNull(report.Hardware);
Assert.False(report.Hardware.IsAzureHosted);
Assert.NotNull(report.Hardware.OllamaModels);
```

- [ ] **Step 3: Run to verify RED**

Run: `dotnet test tests/PoMode.Integration --filter HardwareProbeTests`
Expected: FAIL — compile errors (`HardwareProbe`, `NvmlInterop` missing).

- [ ] **Step 4: Implement**

`src/PoMode.API/Infrastructure/EnvironmentDetector.cs`:
```csharp
namespace PoMode.API.Infrastructure;

public static class EnvironmentDetector
{
    public static bool IsAzureHosted() =>
        Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") is not null
        || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
}
```

`src/PoMode.API/Features/Hardware/NvmlInterop.cs`:
```csharp
using System.Runtime.InteropServices;
using PoMode.Shared.Hardware;

namespace PoMode.API.Features.Hardware;

/// <summary>Thin NVML wrapper. Best-effort: any missing DLL/entry point or non-zero return yields null.</summary>
public static partial class NvmlInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [LibraryImport("nvml", EntryPoint = "nvmlInit_v2")]
    private static partial int NvmlInit();

    [LibraryImport("nvml", EntryPoint = "nvmlShutdown")]
    private static partial int NvmlShutdown();

    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static partial int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [LibraryImport("nvml", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static partial int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    public static GpuReport? TryProbe()
    {
        try
        {
            if (NvmlInit() != 0)
            {
                return null;
            }
            try
            {
                if (NvmlDeviceGetHandleByIndex(0, out var device) != 0
                    || NvmlDeviceGetMemoryInfo(device, out var memory) != 0)
                {
                    return null;
                }
                return new GpuReport(
                    Vendor: "NVIDIA",
                    TotalVramMb: (long)(memory.Total / (1024 * 1024)),
                    FreeVramMb: (long)(memory.Free / (1024 * 1024)),
                    CudaAvailable: true,
                    DmlAvailable: OperatingSystem.IsWindows());
            }
            finally
            {
                _ = NvmlShutdown();
            }
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }
}
```

`src/PoMode.API/Features/Hardware/HardwareProbe.cs`:
```csharp
using System.Text.Json;
using PoMode.API.Infrastructure;
using PoMode.Shared.Hardware;

namespace PoMode.API.Features.Hardware;

/// <summary>Runtime capability probe feeding /diag and (in later phases) executor availability.</summary>
public sealed class HardwareProbe(IConfiguration configuration, IHttpClientFactory httpClientFactory)
{
    private static readonly string[] ProviderKeyNames = ["ReplicateApiToken", "SonicApiKey", "LalalApiKey"];

    public async Task<HardwareReport> ProbeAsync(CancellationToken ct)
    {
        var isAzure = EnvironmentDetector.IsAzureHosted();
        var gpu = isAzure ? null : NvmlInterop.TryProbe();
        var ollamaModels = isAzure ? [] : await ProbeOllamaAsync(ct);
        var providers = ProviderKeyNames
            .Where(key => !string.IsNullOrEmpty(configuration[key]))
            .ToArray();
        return new HardwareReport(isAzure, gpu, ollamaModels, providers);
    }

    private async Task<IReadOnlyList<string>> ProbeOllamaAsync(CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("ollama-probe");
            client.Timeout = TimeSpan.FromSeconds(1);
            using var response = await client.GetAsync("http://localhost:11434/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.GetProperty("models").EnumerateArray()
                .Select(model => model.GetProperty("name").GetString())
                .Where(name => name is not null)
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return [];
        }
    }
}
```

`src/PoMode.API/Features/Hardware/DiagnosticsService.cs` — replace with the async version:
```csharp
using PoMode.API.Infrastructure;
using PoMode.Shared.Diagnostics;

namespace PoMode.API.Features.Hardware;

/// <summary>Builds the /diag report. Reports secret PRESENCE only — never values.</summary>
public sealed class DiagnosticsService(
    IConfiguration configuration,
    IHostEnvironment environment,
    SecretSourceInfo secretSource,
    HardwareProbe hardwareProbe)
{
    private static readonly string[] ProviderKeyNames = ["ReplicateApiToken", "SonicApiKey", "LalalApiKey"];

    public async Task<DiagnosticsReport> BuildReportAsync(CancellationToken ct) => new(
        EnvironmentName: environment.EnvironmentName,
        IsAzureHosted: EnvironmentDetector.IsAzureHosted(),
        SecretSource: secretSource.Source.ToString(),
        SecretFellBack: secretSource.FellBack,
        ProviderKeys: ProviderKeyNames
            .Select(name => new ProviderKeyStatus(name, !string.IsNullOrEmpty(configuration[name])))
            .ToArray(),
        Hardware: await hardwareProbe.ProbeAsync(ct));
}
```

`src/PoMode.API/Features/Hardware/DiagnosticsEndpoints.cs` — the `/diag` handler becomes:
```csharp
group.MapGet("", async (DiagnosticsService diagnostics, CancellationToken ct)
    => TypedResults.Ok(await diagnostics.BuildReportAsync(ct)));
```

`src/PoMode.API/Program.cs` — beside the existing `DiagnosticsService` registration, add:
```csharp
builder.Services.AddHttpClient();
builder.Services.AddSingleton<HardwareProbe>();
```

- [ ] **Step 5: Run to verify GREEN**

Run: `dotnet test`
Expected: all green (incl. 3 new Integration + extended E2EAPI diag test), zero warnings. On this dev machine the diag response should show a real NVIDIA GpuReport and installed Ollama models — paste one `/diag` sample into the report as evidence (values, not secrets).

- [ ] **Step 6: Commit**

```powershell
git add src/PoMode.API tests/PoMode.Integration tests/PoMode.E2EAPI
git commit -m "feat: NVML and Ollama hardware probe wired into /diag"
```

---

### Task 7: AnalysisPipeline + Queue + Worker (Restart-Safe, Fallback, Cancellable)

**Files:**
- Create: `src/PoMode.API/Features/Analysis/AnalysisPipeline.cs`, `src/PoMode.API/Features/Analysis/JobQueue.cs`, `src/PoMode.API/Features/Analysis/JobCancellationRegistry.cs`, `src/PoMode.API/Features/Analysis/AnalysisWorker.cs`, `src/PoMode.API/Features/Analysis/IAnalysisNotifier.cs`
- Test: `tests/PoMode.Integration/AnalysisPipelineTests.cs`

**Interfaces:**
- Consumes: `JobStore`/`JobState` (Task 3), stage contracts + fakes (Task 4), `ExecutionPlanner` (Task 5).
- Produces:
  - `interface IAnalysisNotifier { Task PublishAsync(JobStatusDto status, CancellationToken ct); }`
  - `JobQueue` — `ValueTask EnqueueAsync(string jobId, CancellationToken)`, `IAsyncEnumerable<string> DequeueAllAsync(CancellationToken)` (bounded channel, capacity 32)
  - `JobCancellationRegistry` — `Register(string, CancellationTokenSource)`, `bool TryCancel(string)`, `Remove(string)`
  - `AnalysisPipeline.RunAsync(string jobId, CancellationToken)` — plans (if `Plan` empty), runs the 4 stages skipping any in `CompletedStages`, persists + publishes after every transition; executor failure falls through to next available candidate by tier rank (plan entry updated); `OperationCanceledException` → `Cancelled`; other exceptions → `Failed` + `Error`; artifacts: `notes.json`/`chords.json` written by pipeline from stage results, `result.json` by the modal analyzer
  - `AnalysisWorker : BackgroundService` — consumes queue, concurrency 1, registers/removes the per-job CTS
  - Stage→progress mapping: entering stage i sets `Progress = i/4.0`; completing sets `(i+1)/4.0`; `Complete` sets 1.0.

- [ ] **Step 1: Write the failing tests** — `tests/PoMode.Integration/AnalysisPipelineTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.StemSeparation;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;

namespace PoMode.Integration;

public sealed class AnalysisPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-pipe-{Guid.NewGuid():N}");
    private readonly JobStore _store;
    private readonly RecordingNotifier _notifier = new();

    public AnalysisPipelineTests()
    {
        _store = new JobStore(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
            TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingNotifier : IAnalysisNotifier
    {
        public List<JobStatusDto> Published { get; } = [];
        public Task PublishAsync(JobStatusDto status, CancellationToken ct)
        {
            lock (Published) Published.Add(status);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingStemSeparator : IStemSeparator
    {
        private readonly FakeStemSeparator _inner = new();
        public int Calls;
        public string Name => nameof(CountingStemSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task SeparateAsync(StageContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return _inner.SeparateAsync(context, ct);
        }
    }

    private sealed class ThrowingStemSeparator : IStemSeparator
    {
        public string Name => nameof(ThrowingStemSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task SeparateAsync(StageContext context, CancellationToken ct)
            => throw new InvalidOperationException("simulated OOM");
    }

    private sealed class HangingStemSeparator : IStemSeparator
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => nameof(HangingStemSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public async Task SeparateAsync(StageContext context, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private AnalysisPipeline Pipeline(IStemSeparator[]? separators = null)
    {
        separators ??= [new FakeStemSeparator()];
        IPitchTracker[] trackers = [new FakePitchTracker()];
        IChordRecognizer[] chords = [new FakeChordRecognizer()];
        return new AnalysisPipeline(
            _store,
            new ExecutionPlanner(separators, trackers, chords),
            separators, trackers, chords,
            new PlaceholderModalAnalyzer(),
            _notifier,
            NullLogger<AnalysisPipeline>.Instance);
    }

    private async Task<JobState> NewJobAsync()
    {
        using var content = new MemoryStream(TestAudio.MakeWav());
        return await _store.CreateAsync("song.wav", content, CancellationToken.None);
    }

    [Fact]
    public async Task Full_run_produces_all_artifacts_and_completes()
    {
        var job = await NewJobAsync();

        await Pipeline().RunAsync(job.JobId, CancellationToken.None);

        var dir = _store.JobDir(job.JobId);
        foreach (var artifact in new[] { "vocals.wav", "instrumental.wav", "notes.json", "chords.json", "result.json" })
        {
            Assert.True(File.Exists(Path.Combine(dir, artifact)), $"missing {artifact}");
        }
        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
        Assert.Equal(1.0, final.Progress);
        Assert.Equal(4, final.CompletedStages.Count);
        Assert.Equal(JobStage.Complete, _notifier.Published[^1].Stage);
    }

    [Fact]
    public async Task Rerun_after_completion_skips_all_stages()
    {
        var job = await NewJobAsync();
        var counter = new CountingStemSeparator();
        var pipeline = Pipeline([counter]);

        await pipeline.RunAsync(job.JobId, CancellationToken.None);
        await pipeline.RunAsync(job.JobId, CancellationToken.None); // simulated restart re-enqueue

        Assert.Equal(1, counter.Calls);
        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
    }

    [Fact]
    public async Task Executor_failure_falls_through_to_next_candidate_and_updates_plan()
    {
        var job = await NewJobAsync();
        // Throwing executor is planned first (registration order breaks the tier tie).
        var pipeline = Pipeline([new ThrowingStemSeparator(), new FakeStemSeparator()]);

        await pipeline.RunAsync(job.JobId, CancellationToken.None);

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
        Assert.Equal(nameof(FakeStemSeparator), final.Plan.Single(p => p.Stage == StageNames.Separating).Executor);
    }

    [Fact]
    public async Task All_candidates_failing_marks_job_failed_with_error()
    {
        var job = await NewJobAsync();
        var pipeline = Pipeline([new ThrowingStemSeparator()]);

        await pipeline.RunAsync(job.JobId, CancellationToken.None);

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Failed, final!.Stage);
        Assert.Contains("simulated OOM", final.Error);
    }

    [Fact]
    public async Task Cancellation_marks_job_cancelled()
    {
        var job = await NewJobAsync();
        var hanging = new HangingStemSeparator();
        var pipeline = Pipeline([hanging]);
        using var cts = new CancellationTokenSource();

        var run = pipeline.RunAsync(job.JobId, cts.Token);
        await hanging.Started.Task;
        cts.Cancel();
        await run;

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Cancelled, final!.Stage);
    }
}
```

- [ ] **Step 2: Run to verify RED**

Run: `dotnet test tests/PoMode.Integration --filter AnalysisPipelineTests`
Expected: FAIL — compile errors (`AnalysisPipeline`, `IAnalysisNotifier` missing).

- [ ] **Step 3: Implement**

`src/PoMode.API/Features/Analysis/IAnalysisNotifier.cs`:
```csharp
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Pipeline-side progress seam; the SignalR implementation lives with the hub (Task 8).</summary>
public interface IAnalysisNotifier
{
    Task PublishAsync(JobStatusDto status, CancellationToken ct);
}
```

`src/PoMode.API/Features/Analysis/JobQueue.cs`:
```csharp
using System.Threading.Channels;

namespace PoMode.API.Features.Analysis;

public sealed class JobQueue
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(string jobId, CancellationToken ct) => _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
```

`src/PoMode.API/Features/Analysis/JobCancellationRegistry.cs`:
```csharp
using System.Collections.Concurrent;

namespace PoMode.API.Features.Analysis;

public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public void Register(string jobId, CancellationTokenSource cts) => _running[jobId] = cts;

    public bool TryCancel(string jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public void Remove(string jobId) => _running.TryRemove(jobId, out _);
}
```

`src/PoMode.API/Features/Analysis/AnalysisPipeline.cs`:
```csharp
using System.Text.Json;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Runs the 4-stage pipeline for one job: restart-safe, tier-fallback on failure, cancellable.</summary>
public sealed class AnalysisPipeline(
    JobStore store,
    ExecutionPlanner planner,
    IEnumerable<IStemSeparator> stemSeparators,
    IEnumerable<IPitchTracker> pitchTrackers,
    IEnumerable<IChordRecognizer> chordRecognizers,
    IModalAnalyzer modalAnalyzer,
    IAnalysisNotifier notifier,
    ILogger<AnalysisPipeline> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task RunAsync(string jobId, CancellationToken ct)
    {
        var state = await store.LoadAsync(jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");
        if (state.Stage is JobStage.Complete or JobStage.Cancelled)
        {
            // A job cancelled while still queued (DELETE before dequeue) or re-enqueued after
            // completion must not run again.
            return;
        }
        try
        {
            if (state.Plan.Count == 0)
            {
                state.Plan = await planner.PlanAsync(ct);
                await PersistAsync(state, ct);
            }

            var context = new StageContext(jobId, store.JobDir(jobId), store.InputPath(state));

            if (!state.CompletedStages.Contains(StageNames.Separating))
            {
                await EnterStageAsync(state, JobStage.Separating, 0, ct);
                await RunWithFallbackAsync(state, StageNames.Separating, stemSeparators,
                    async (executor, token) => { await executor.SeparateAsync(context, token); return true; }, ct);
                await CompleteStageAsync(state, StageNames.Separating, 0, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.PitchTracking))
            {
                await EnterStageAsync(state, JobStage.PitchTracking, 1, ct);
                var notes = await RunWithFallbackAsync(state, StageNames.PitchTracking, pitchTrackers,
                    (executor, token) => executor.TrackAsync(context, token), ct);
                await WriteArtifactAsync(context.JobDir, "notes.json", notes, ct);
                await CompleteStageAsync(state, StageNames.PitchTracking, 1, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.ChordDetecting))
            {
                await EnterStageAsync(state, JobStage.ChordDetecting, 2, ct);
                var chords = await RunWithFallbackAsync(state, StageNames.ChordDetecting, chordRecognizers,
                    (executor, token) => executor.RecognizeAsync(context, token), ct);
                await WriteArtifactAsync(context.JobDir, "chords.json", chords, ct);
                await CompleteStageAsync(state, StageNames.ChordDetecting, 2, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.ModalAnalysis))
            {
                await EnterStageAsync(state, JobStage.ModalAnalysis, 3, ct);
                await modalAnalyzer.AnalyzeAsync(context, ct);
                await CompleteStageAsync(state, StageNames.ModalAnalysis, 3, ct);
            }

            state.Stage = JobStage.Complete;
            state.Progress = 1.0;
            await PersistAsync(state, ct);
        }
        catch (OperationCanceledException)
        {
            state.Stage = JobStage.Cancelled;
            await PersistAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed in stage {Stage}.", jobId, state.Stage);
            state.Stage = JobStage.Failed;
            state.Error = ex.Message;
            await PersistAsync(state, CancellationToken.None);
        }
    }

    private async Task<TResult> RunWithFallbackAsync<TExecutor, TResult>(
        JobState state,
        string stage,
        IEnumerable<TExecutor> candidates,
        Func<TExecutor, CancellationToken, Task<TResult>> run,
        CancellationToken ct)
        where TExecutor : IStageExecutor
    {
        var planned = state.Plan.Single(p => p.Stage == stage);
        var ordered = candidates
            .OrderBy(c => c.Name == planned.Executor ? -1 : ExecutionPlanner.TierRank(c.Tier))
            .ToList();

        Exception? lastFailure = null;
        foreach (var candidate in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (lastFailure is not null && !await candidate.IsAvailableAsync(ct))
            {
                continue;
            }
            try
            {
                var result = await run(candidate, ct);
                if (candidate.Name != planned.Executor)
                {
                    state.Plan[state.Plan.IndexOf(planned)] = planned with { Tier = candidate.Tier, Executor = candidate.Name };
                    await PersistAsync(state, ct);
                    logger.LogWarning("Stage {Stage} fell back from {Planned} to {Actual}.", stage, planned.Executor, candidate.Name);
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Executor {Executor} failed for stage {Stage}; trying next tier.", candidate.Name, stage);
                lastFailure = ex;
            }
        }
        throw lastFailure ?? new InvalidOperationException($"No executor ran for stage {stage}.");
    }

    private async Task EnterStageAsync(JobState state, JobStage stage, int index, CancellationToken ct)
    {
        state.Stage = stage;
        state.Progress = index / 4.0;
        await PersistAsync(state, ct);
    }

    private async Task CompleteStageAsync(JobState state, string stageName, int index, CancellationToken ct)
    {
        state.CompletedStages.Add(stageName);
        state.Progress = (index + 1) / 4.0;
        await PersistAsync(state, ct);
    }

    private async Task PersistAsync(JobState state, CancellationToken ct)
    {
        await store.SaveAsync(state, ct);
        await notifier.PublishAsync(state.ToDto(), ct);
    }

    private static Task WriteArtifactAsync<T>(string jobDir, string fileName, T payload, CancellationToken ct)
        => File.WriteAllTextAsync(Path.Combine(jobDir, fileName), JsonSerializer.Serialize(payload, JsonOptions), ct);
}
```

`src/PoMode.API/Features/Analysis/AnalysisWorker.cs`:
```csharp
namespace PoMode.API.Features.Analysis;

/// <summary>Single-concurrency job consumer — GPU stages must not share VRAM.</summary>
public sealed class AnalysisWorker(
    JobQueue queue,
    AnalysisPipeline pipeline,
    JobCancellationRegistry cancellations,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.DequeueAllAsync(stoppingToken))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cancellations.Register(jobId, cts);
            try
            {
                await pipeline.RunAsync(jobId, cts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled pipeline failure for job {JobId}.", jobId);
            }
            finally
            {
                cancellations.Remove(jobId);
            }
        }
    }
}
```

- [ ] **Step 4: Run to verify GREEN**

Run: `dotnet test tests/PoMode.Integration --filter AnalysisPipelineTests` then full `dotnet test`
Expected: 6 new pass; all green, zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.API/Features/Analysis tests/PoMode.Integration
git commit -m "feat: restart-safe analysis pipeline with tier fallback, queue, and worker"
```

---

### Task 8: Analysis Endpoints + SignalR Hub + Cleanup Service + Wiring

**Files:**
- Create: `src/PoMode.API/Features/Analysis/AnalysisEndpoints.cs`, `src/PoMode.API/Features/Analysis/AnalysisHub.cs`, `src/PoMode.API/Features/Analysis/JobCleanupService.cs`
- Modify: `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.E2EAPI/AnalysisApiTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–7.
- Produces:
  - `POST /api/analysis` (multipart, any field name; first file used) → 200 `JobStatusDto` | 400 (`"No file uploaded."` / `"Only .mp3 and .wav files are supported."` / `"File exceeds the 100 MB limit."`)
  - `GET /api/analysis/{jobId}` → 200 `JobStatusDto` | 404
  - `DELETE /api/analysis/{jobId}` → 200 (cancel requested or already-terminal) | 404
  - `GET /api/analysis/{jobId}/notes` and `/chords` → 200 JSON file | 404
  - Hub `/hubs/analysis`: client calls `Subscribe(jobId)`; server pushes `JobStatusChanged(JobStatusDto)` to group `job-{jobId}`
  - `SignalRAnalysisNotifier : IAnalysisNotifier` (in AnalysisHub.cs)
  - `JobCleanupService : BackgroundService` — purges jobs older than 7 days, hourly check
  - All DI + `FormOptions.MultipartBodyLengthLimit = AudioFormatValidator.MaxBytes`

- [ ] **Step 1: Write the failing tests** — `tests/PoMode.E2EAPI/AnalysisApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;

namespace PoMode.E2EAPI;

public sealed class AnalysisApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.UseSetting("Jobs:RootPath", _root));

    private static MultipartFormDataContent WavForm(byte[]? bytes = null)
    {
        var content = new ByteArrayContent(bytes ?? TestAudio.MakeWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new MultipartFormDataContent { { content, "file", "test.wav" } };
    }

    [Fact]
    public async Task Upload_returns_job_status_and_job_completes_via_hub_or_polling()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, "/hubs/analysis"),
                options => options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler())
            .Build();
        var terminal = new TaskCompletionSource<JobStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JobStatusDto>("JobStatusChanged", status =>
        {
            if (status.Stage is JobStage.Complete or JobStage.Failed) terminal.TrySetResult(status);
        });
        await hub.StartAsync();

        using var form = WavForm();
        var response = await client.PostAsync("/api/analysis", form);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(created);
        Assert.Equal(4, created.Plan.Count);
        await hub.InvokeAsync("Subscribe", created.JobId);

        // The fake pipeline may finish before Subscribe lands — poll as a fallback.
        var final = await WaitForTerminalAsync(client, created.JobId, terminal.Task);
        Assert.Equal(JobStage.Complete, final.Stage);

        var notes = await client.GetFromJsonAsync<List<NoteEvent>>($"/api/analysis/{created.JobId}/notes");
        var chords = await client.GetFromJsonAsync<List<ChordSpan>>($"/api/analysis/{created.JobId}/chords");
        Assert.Equal(8, notes!.Count);
        Assert.Equal(4, chords!.Count);
    }

    private static async Task<JobStatusDto> WaitForTerminalAsync(
        HttpClient client, string jobId, Task<JobStatusDto> hubSignal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (hubSignal.IsCompleted) return await hubSignal;
            var status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{jobId}");
            if (status!.Stage is JobStage.Complete or JobStage.Failed or JobStage.Cancelled) return status;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Job {jobId} did not reach a terminal stage in 15s.");
    }

    [Fact]
    public async Task Upload_without_file_is_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/analysis", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_of_non_audio_content_is_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var form = WavForm([0x25, 0x50, 0x44, 0x46, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);
        var response = await client.PostAsync("/api/analysis", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("supported", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Status_of_unknown_job_is_404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/analysis/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope/notes")).StatusCode);
    }
}
```

- [ ] **Step 2: Add the SignalR client package to E2EAPI**

```powershell
dotnet add tests/PoMode.E2EAPI package Microsoft.AspNetCore.SignalR.Client
```

- [ ] **Step 3: Run to verify RED**

Run: `dotnet test tests/PoMode.E2EAPI --filter AnalysisApiTests`
Expected: FAIL — 404s (endpoints don't exist).

- [ ] **Step 4: Implement**

`src/PoMode.API/Features/Analysis/AnalysisHub.cs`:
```csharp
using Microsoft.AspNetCore.SignalR;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

public sealed class AnalysisHub : Hub
{
    public static string GroupName(string jobId) => $"job-{jobId}";

    public Task Subscribe(string jobId)
        => Groups.AddToGroupAsync(Context.ConnectionId, GroupName(jobId));
}

public sealed class SignalRAnalysisNotifier(IHubContext<AnalysisHub> hubContext) : IAnalysisNotifier
{
    public Task PublishAsync(JobStatusDto status, CancellationToken ct)
        => hubContext.Clients.Group(AnalysisHub.GroupName(status.JobId))
            .SendAsync("JobStatusChanged", status, ct);
}
```

`src/PoMode.API/Features/Analysis/JobCleanupService.cs`:
```csharp
namespace PoMode.API.Features.Analysis;

/// <summary>Hourly sweep deleting job folders older than 7 days.</summary>
public sealed class JobCleanupService(JobStore store, ILogger<JobCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var purged = store.PurgeOlderThan(TimeSpan.FromDays(7));
                if (purged > 0)
                {
                    logger.LogInformation("Purged {Count} expired job folder(s).", purged);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job cleanup sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

`src/PoMode.API/Features/Analysis/AnalysisEndpoints.cs`:
```csharp
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysis(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis");

        group.MapPost("", async Task<Results<Ok<JobStatusDto>, BadRequest<string>>> (
            HttpRequest request, JobStore store, JobQueue queue, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return TypedResults.BadRequest("Expected a multipart form upload.");
            }
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                return TypedResults.BadRequest("No file uploaded.");
            }
            if (file.Length > AudioFormatValidator.MaxBytes)
            {
                return TypedResults.BadRequest("File exceeds the 100 MB limit.");
            }

            await using var stream = file.OpenReadStream();
            var header = new byte[12];
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (!AudioFormatValidator.IsSupported(header.AsSpan(0, read), out _))
            {
                return TypedResults.BadRequest("Only .mp3 and .wav files are supported.");
            }

            await using var fresh = file.OpenReadStream();
            var state = await store.CreateAsync(file.FileName, fresh, ct);
            await queue.EnqueueAsync(state.JobId, ct);
            return TypedResults.Ok(state.ToDto());
        }).DisableAntiforgery();

        group.MapGet("/{jobId}", async Task<Results<Ok<JobStatusDto>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(jobId, ct);
            return state is null ? TypedResults.NotFound() : TypedResults.Ok(state.ToDto());
        });

        group.MapDelete("/{jobId}", async Task<Results<Ok, NotFound>> (
            string jobId, JobStore store, JobCancellationRegistry cancellations, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(jobId, ct);
            if (state is null)
            {
                return TypedResults.NotFound();
            }
            if (!cancellations.TryCancel(jobId)
                && state.Stage is not (JobStage.Complete or JobStage.Failed or JobStage.Cancelled))
            {
                state.Stage = JobStage.Cancelled;
                await store.SaveAsync(state, ct);
            }
            return TypedResults.Ok();
        });

        MapArtifact(group, "notes", "notes.json");
        MapArtifact(group, "chords", "chords.json");
        return app;
    }

    private static void MapArtifact(RouteGroupBuilder group, string route, string fileName)
        => group.MapGet($"/{{jobId}}/{route}", Results<PhysicalFileHttpResult, NotFound> (string jobId, JobStore store) =>
        {
            var path = Path.Combine(store.JobDir(jobId), fileName);
            return File.Exists(path)
                ? TypedResults.PhysicalFile(path, "application/json")
                : TypedResults.NotFound();
        });
}
```

Add `using Microsoft.AspNetCore.Http.HttpResults;` at the top of `AnalysisEndpoints.cs`.

`src/PoMode.API/Program.cs` — add to services (beside the diagnostics registrations):
```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddSingleton<IStemSeparator, FakeStemSeparator>();
builder.Services.AddSingleton<IPitchTracker, FakePitchTracker>();
builder.Services.AddSingleton<IChordRecognizer, FakeChordRecognizer>();
builder.Services.AddSingleton<IModalAnalyzer, PlaceholderModalAnalyzer>();
builder.Services.AddSingleton<ExecutionPlanner>();
builder.Services.AddSingleton<IAnalysisNotifier, SignalRAnalysisNotifier>();
builder.Services.AddSingleton<AnalysisPipeline>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<JobCleanupService>();
builder.Services.AddSignalR();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(
    options => options.MultipartBodyLengthLimit = AudioFormatValidator.MaxBytes);
```
and to the pipeline (before `app.MapFallbackToFile("index.html");`):
```csharp
app.MapAnalysis();
app.MapHub<AnalysisHub>("/hubs/analysis");
```
with usings: `PoMode.API.Features.Analysis`, `PoMode.API.Features.ChordRecognition`, `PoMode.API.Features.ModalAnalysis`, `PoMode.API.Features.PitchTracking`, `PoMode.API.Features.StemSeparation`, `PoMode.API.Pipeline`.

- [ ] **Step 5: Run to verify GREEN**

Run: `dotnet test tests/PoMode.E2EAPI --filter AnalysisApiTests` then full `dotnet test`
Expected: 4 new pass; all green, zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/PoMode.API tests/PoMode.E2EAPI
git commit -m "feat: analysis upload/status/cancel endpoints with SignalR progress and job cleanup"
```

---

### Task 9: Client — Upload, Live Progress, Results

**Files:**
- Create: `src/PoMode.Client/Services/AnalysisClient.cs`, `src/PoMode.Client/Components/JobProgress.razor`, `src/PoMode.Client/Components/JobProgress.razor.css`
- Modify: `src/PoMode.Client/Pages/Home.razor` (+ create `Home.razor.css`), `src/PoMode.Client/Services/MockDataState.cs`, `src/PoMode.Client/Components/MockDataBanner.razor`, `src/PoMode.Client/Program.cs`, `src/PoMode.Client/_Imports.razor`

**Interfaces:**
- Consumes: `/api/analysis` surface + hub (Task 8), `JobStatusDto`/`NoteEvent`/`ChordSpan` (Task 1), `MockDataState` (Phase 1).
- Produces:
  - `AnalysisClient(HttpClient)` — `GetStatusAsync`, `GetNotesAsync`, `GetChordsAsync` (all by jobId, camelCase web JSON)
  - `MockDataState` gains `event Action? Changed` and `SetLive()` (sets `IsMockData = false`, raises event); banner subscribes/unsubscribes
  - `JobProgress` component: stage rows from `Plan` with tier badge (💻 Local / 🌐 Browser / ☁️💰 Cloud), ✓ for completed, highlight for current; error text when Failed; renders "Analysis complete" when Complete (E2EUI hooks on this text)
  - Home page: RadzenUpload (`Url="api/analysis"`, auto), hub subscription on upload completion, results card ("`{n}` notes · `{m}` chords" + first notes/chords lists)

- [ ] **Step 1: Add the SignalR client package**

```powershell
dotnet add src/PoMode.Client package Microsoft.AspNetCore.SignalR.Client
```

- [ ] **Step 2: Implement services**

`src/PoMode.Client/Services/AnalysisClient.cs`:
```csharp
using System.Net.Http.Json;
using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

public sealed class AnalysisClient(HttpClient http)
{
    public Task<JobStatusDto?> GetStatusAsync(string jobId)
        => http.GetFromJsonAsync<JobStatusDto>($"api/analysis/{jobId}");

    public Task<List<NoteEvent>?> GetNotesAsync(string jobId)
        => http.GetFromJsonAsync<List<NoteEvent>>($"api/analysis/{jobId}/notes");

    public Task<List<ChordSpan>?> GetChordsAsync(string jobId)
        => http.GetFromJsonAsync<List<ChordSpan>>($"api/analysis/{jobId}/chords");
}
```

`src/PoMode.Client/Services/MockDataState.cs` (replace):
```csharp
namespace PoMode.Client.Services;

/// <summary>True whenever displayed analysis data is mock/local. Real job results flip it via SetLive().</summary>
public sealed class MockDataState
{
    public bool IsMockData { get; private set; } = true;

    public event Action? Changed;

    public void SetLive()
    {
        if (IsMockData)
        {
            IsMockData = false;
            Changed?.Invoke();
        }
    }
}
```

`src/PoMode.Client/Components/MockDataBanner.razor` (replace):
```razor
@inject MockDataState MockData
@implements IDisposable

@if (MockData.IsMockData)
{
    <div class="mock-banner" role="alert">USING MOCK DATA</div>
}

@code {
    protected override void OnInitialized() => MockData.Changed += OnChanged;
    private void OnChanged() => InvokeAsync(StateHasChanged);
    public void Dispose() => MockData.Changed -= OnChanged;
}
```

`src/PoMode.Client/Program.cs` — add beside the `MockDataState` registration:
```csharp
builder.Services.AddScoped<AnalysisClient>();
```

- [ ] **Step 3: Implement the progress component**

`src/PoMode.Client/Components/JobProgress.razor`:
```razor
@using PoMode.Shared.Analysis

<RadzenCard class="job-progress">
    <RadzenText TextStyle="TextStyle.H6" Text="Analysis progress" />
    <RadzenProgressBar Value="@((Status.Progress) * 100)" ShowValue="false" />
    <ul class="stages">
        @foreach (var stage in Status.Plan)
        {
            <li class="@StageClass(stage)">
                <span class="tier">@TierBadge(stage.Tier)</span>
                <span class="name">@stage.Stage</span>
                <span class="executor">@stage.Executor</span>
                @if (Status.CompletedStages.Contains(stage.Stage))
                {
                    <span class="done">✓</span>
                }
            </li>
        }
    </ul>
    @if (Status.Stage == JobStage.Complete)
    {
        <RadzenText class="complete-text" Text="Analysis complete" />
    }
    else if (Status.Stage == JobStage.Failed)
    {
        <RadzenText class="error-text" Text="@($"Analysis failed: {Status.Error}")" />
    }
    else if (Status.Stage == JobStage.Cancelled)
    {
        <RadzenText Text="Analysis cancelled" />
    }
</RadzenCard>

@code {
    [Parameter, EditorRequired]
    public required JobStatusDto Status { get; set; }

    private string StageClass(StagePlan stage)
        => Status.CompletedStages.Contains(stage.Stage) ? "stage completed"
            : Status.Stage.ToString() == stage.Stage ? "stage active"
            : "stage";

    private static string TierBadge(ExecutionTier tier) => tier switch
    {
        ExecutionTier.Local => "💻",
        ExecutionTier.ClientDelegated => "🌐",
        ExecutionTier.Cloud => "☁️💰",
        _ => "?",
    };
}
```

`src/PoMode.Client/Components/JobProgress.razor.css`:
```css
.stages {
    list-style: none;
    margin: 0.5rem 0 0;
    padding: 0;
}

.stage {
    display: flex;
    gap: 0.5rem;
    align-items: baseline;
    padding: 0.25rem 0;
    color: var(--pm-fg-muted);
}

.stage.active {
    color: var(--pm-fg);
    font-weight: 600;
}

.stage.completed {
    color: var(--pm-fg);
}

.executor {
    font-size: 0.8rem;
    color: var(--pm-fg-muted);
}

.done {
    color: var(--pm-accent);
    font-weight: 700;
}

.error-text {
    color: var(--pm-warn-bg);
}
```

- [ ] **Step 4: Rewrite the Home page**

`src/PoMode.Client/Pages/Home.razor`:
```razor
@page "/"
@using System.Text.Json
@using Microsoft.AspNetCore.SignalR.Client
@using PoMode.Shared.Analysis
@inject AnalysisClient Analysis
@inject MockDataState MockData
@inject NavigationManager Navigation
@implements IAsyncDisposable

<PageTitle>PoMode</PageTitle>

<RadzenCard>
    <RadzenText TextStyle="TextStyle.H5" Text="Audio Modal Analyzer" />
    <RadzenText Text="Upload an .mp3 or .wav track (max 100 MB) to analyze its melody, chords, and scale modes." />
    <RadzenUpload Url="api/analysis" Accept=".mp3,.wav" Auto="true"
                  Complete="@OnUploadComplete" Error="@OnUploadError" ChooseText="Choose audio file" />
    @if (_uploadError is not null)
    {
        <RadzenText class="upload-error" Text="@_uploadError" />
    }
</RadzenCard>

@if (_status is not null)
{
    <JobProgress Status="_status" />
}

@if (_notes is not null && _chords is not null)
{
    <RadzenCard>
        <RadzenText TextStyle="TextStyle.H6" Text="@($"{_notes.Count} notes · {_chords.Count} chords")" />
        <div class="results">
            <ul class="result-list">
                @foreach (var note in _notes)
                {
                    <li>MIDI @note.MidiPitch at @note.StartSec.ToString("0.00")s (@note.DurationSec.ToString("0.00")s)</li>
                }
            </ul>
            <ul class="result-list">
                @foreach (var chord in _chords)
                {
                    <li>@chord.Symbol (@chord.StartSec.ToString("0")–@chord.EndSec.ToString("0")s)</li>
                }
            </ul>
        </div>
    </RadzenCard>
}

@code {
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private JobStatusDto? _status;
    private List<NoteEvent>? _notes;
    private List<ChordSpan>? _chords;
    private string? _uploadError;
    private HubConnection? _hub;

    private async Task OnUploadComplete(UploadCompleteEventArgs args)
    {
        _uploadError = null;
        _notes = null;
        _chords = null;
        var status = JsonSerializer.Deserialize<JobStatusDto>(args.RawResponse, WebJson);
        if (status is null)
        {
            _uploadError = "Unexpected server response.";
            return;
        }
        _status = status;

        if (_hub is null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(Navigation.ToAbsoluteUri("/hubs/analysis"))
                .Build();
            _hub.On<JobStatusDto>("JobStatusChanged", OnStatusChanged); // register once, not per upload
        }
        if (_hub.State == HubConnectionState.Disconnected)
        {
            await _hub.StartAsync();
        }
        await _hub.InvokeAsync("Subscribe", status.JobId);

        // The fake pipeline can finish before Subscribe lands — reconcile once.
        var current = await Analysis.GetStatusAsync(status.JobId);
        if (current is not null)
        {
            await OnStatusChanged(current);
        }
    }

    private async Task OnStatusChanged(JobStatusDto status)
    {
        if (_status?.JobId != status.JobId)
        {
            return;
        }
        _status = status;
        if (status.Stage == JobStage.Complete && _notes is null)
        {
            _notes = await Analysis.GetNotesAsync(status.JobId);
            _chords = await Analysis.GetChordsAsync(status.JobId);
            MockData.SetLive();
        }
        await InvokeAsync(StateHasChanged);
    }

    private void OnUploadError(UploadErrorEventArgs args)
    {
        _uploadError = string.IsNullOrWhiteSpace(args.Message) ? "Upload failed." : args.Message;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }
    }
}
```

`src/PoMode.Client/Pages/Home.razor.css`:
```css
.results {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 1rem;
}

.result-list {
    margin: 0;
    padding-left: 1.25rem;
}

.upload-error {
    color: var(--pm-warn-bg);
}
```

`src/PoMode.Client/_Imports.razor` — no change needed beyond what exists (component namespaces already imported); verify `PoMode.Client.Services` is present.

- [ ] **Step 5: Verify build + all existing tests**

Run: `dotnet build` then `dotnet test`
Expected: zero warnings; all tests green (behavior is covered by Task 10's browser test; this task must not break the existing E2EUI shell test).

- [ ] **Step 6: Commit**

```powershell
git add src/PoMode.Client
git commit -m "feat: upload, live SignalR progress, and results display in the Blazor client"
```

---

### Task 10: E2EUI — Browser Upload Flow

**Files:**
- Create: `tests/PoMode.E2EUI/UploadFlowTests.cs`

**Interfaces:**
- Consumes: `AppFixture` (Phase 1), `TestAudio` (linked in Task 2), the Task-9 UI.
- Produces: browser-level proof of the whole phase: upload → progress → "Analysis complete" → results → mock banner gone.

- [ ] **Step 1: Write the test** — `tests/PoMode.E2EUI/UploadFlowTests.cs`:

```csharp
using Microsoft.Playwright;
using PoMode.TestCommon;

namespace PoMode.E2EUI;

[Collection("App")]
public class UploadFlowTests(AppFixture app)
{
    [Fact]
    public async Task Upload_runs_pipeline_shows_results_and_clears_mock_banner()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-upload-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, TestAudio.MakeWav(seconds: 0.5));
        try
        {
            var visible = new LocatorAssertionsToBeVisibleOptions { Timeout = 30000f };
            await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeVisibleAsync(visible);

            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);

            await Assertions.Expect(page.GetByText("Analysis complete")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("8 notes · 4 chords")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("USING MOCK DATA"))
                .ToBeHiddenAsync(new() { Timeout = 30000f });
        }
        finally
        {
            File.Delete(wavPath);
        }
    }
}
```

- [ ] **Step 2: Run to verify (RED first if Task 9 unmerged, GREEN after)**

Run: `dotnet test tests/PoMode.E2EUI`
Expected: 2/2 pass (shell smoke + upload flow). If the upload-flow test fails, use superpowers:systematic-debugging — no sleeps, no weakened assertions. Known pitfall: if RadzenUpload posts a field name the endpoint doesn't see, remember the endpoint reads `form.Files.FirstOrDefault()` regardless of field name.

- [ ] **Step 3: Full-solution verification**

Run: `dotnet test`
Expected: every test in all four projects passes; zero warnings.

- [ ] **Step 4: Commit**

```powershell
git add tests/PoMode.E2EUI
git commit -m "test: browser upload flow through the fake pipeline"
```

---

## Phase 2 Exit Criteria

- `dotnet test` green across all four projects; zero build warnings; no `Version=` in csproj; no secrets tracked.
- Manual: `dotnet run --project src/PoMode.API` → upload a real .mp3/.wav in the browser → live stage progress with tier badges → "Analysis complete", notes/chords listed, mock banner gone; `/diag` shows the real GPU + Ollama report.
- Restart-safety proven by test (`Rerun_after_completion_skips_all_stages`); cancel via `DELETE /api/analysis/{id}`.
- Phase 3 starts from: `IModalAnalyzer` seam (replace `PlaceholderModalAnalyzer`), `notes.json`/`chords.json` artifacts, `StageContext`, and the modal-mask table in spec §6.
