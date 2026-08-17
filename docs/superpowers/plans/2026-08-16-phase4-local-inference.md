# PoMode Phase 4: Local Inference Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the fake executors with real local ONNX inference on this Snapdragon X Elite (win-arm64, CPU EP): decode real audio, separate stems, track pitch with Basic Pitch, estimate tempo — so an uploaded song produces genuinely its own notes, its own BPM, and a MIDI file that matches what was sung.

**Architecture:** A new `Features/Audio/` slice decodes any supported upload to float PCM. `Infrastructure/ModelRegistry` downloads and SHA-256-verifies ONNX models on first use into `models/`. Each real executor (`OnnxStemSeparator`, `OnnxPitchTracker`) implements the Phase-2 stage interface and reports `IsAvailableAsync` based on the registry, so `ExecutionPlanner` picks it over the fake automatically and falls back untouched when a model is missing. A deterministic `TempoEstimator` (no model) finally supplies the real BPM that Phase 3 stubbed at 120.

**Tech Stack:** .NET 10, `Microsoft.ML.OnnxRuntime` 1.29.0 (CPU EP — proven fastest here), `NAudio` + `NLayer` (decode), xUnit, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-16-pomode-design.md` (§4 tier routing, §5 stage integrations, §13.1 hardware correction, §13.4 the artifact race).

**Spike evidence this plan is built on:** `.superpowers/spikes/2026-08-16-onnx-arm64-spike.md` — on this machine all three EPs load, and the real Basic Pitch model runs at **12.3 ms per 2-second window on the CPU EP**, *faster* than DirectML (40.6 ms). Outputs matched across EPs.

**Plan-level rulings (decided now):**
- **CPU EP only.** DirectML was measurably slower and mixing its package with the CPU package caused an `onnxruntime.dll` collision (`EntryPointNotFoundException`). QNN/NPU needs an int8 quantization pipeline we are not building. One package: `Microsoft.ML.OnnxRuntime`.
- **User accepted slow stem separation** (asked twice). We measure and report honestly; we do not silently route to the cloud.
- **Chord recognition (BTC) is NOT in this phase.** It is the highest-risk export and the fake recognizer keeps working. Phase 5.
- Task 2 is a **go/no-go gate** on the stem model. If no usable ONNX stem model can be obtained and run, Tasks 3–7 still deliver real decode, real pitch tracking and real tempo; only Task 8 is dropped, and the fake separator stays. That outcome is a success, not a failure — record it and continue.
- Models are never committed. `models/` is already git-ignored.

## Global Constraints

- All prior constraints hold: `net10.0`, Nullable + TreatWarningsAsErrors from `Directory.Build.props` only; CPM (versions ONLY in `Directory.Packages.props`, added via `dotnet add`); `PoMode.` prefixes; no secrets; endpoints via `MapGroup()` + `TypedResults`; zero inline CSS.
- **TDD with log-file evidence is mandatory.** Tee every RED and GREEN run to `<workspace>/task-N-{red,green,full}.log` and quote them; reviewers read the logs. **Check the full log yourself before reporting DONE** — it must show four `Passed!` lines with `Failed: 0`.
- **Known environment quirk:** running the whole solution at once can starve the browser tests on this box. If `PoMode.E2EUI` fails inside a full-solution run, re-run `dotnet test tests/PoMode.E2EUI` alone before concluding anything is broken, and report both results.
- Long-running tests: any test that runs a real model must carry `[Trait("Category", "Slow")]` so the fast suite stays fast.
- Commit hygiene: stage only each task's listed paths. Never stage `.claude/`, `.superpowers/`, `models/`, `*.mp3`, `*.wav` (except the tracked test fixtures under `tests/`).
- Commits: conventional style ending with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- Run all commands from repo root `c:\Users\punko\Downloads\PoMode`.

---

### Task 1: Close the Artifact Read/Write Race

**Files:**
- Modify: `src/PoMode.API/Features/Analysis/JobStore.cs`, `src/PoMode.API/Features/Analysis/AnalysisPipeline.cs`, `src/PoMode.API/Features/ModalAnalysis/ArtifactModalAnalyzer.cs`, `src/PoMode.API/Features/Analysis/AnalysisEndpoints.cs`, `src/PoMode.API/Features/MidiExport/MidiExportEndpoints.cs`
- Test: `tests/PoMode.Integration/JobStoreArtifactTests.cs`

**Why first:** spec §13.4. Artifacts are written unguarded while `TypedResults.PhysicalFile` streams them with its own handle; on Windows a write over an open handle throws `UnauthorizedAccessException`, which the pipeline's catch-all turns into a bogus `Stage = Failed`. Every later task in this phase adds slower stages and bigger files, which widens that window.

**Interfaces:**
- Consumes: existing `JobStore` per-job `SemaphoreSlim`.
- Produces (used by every later task):
  - `Task JobStore.WriteArtifactAsync<T>(string jobId, string fileName, T payload, CancellationToken ct)`
  - `Task<IReadOnlyList<T>> JobStore.ReadArtifactListAsync<T>(string jobId, string fileName, CancellationToken ct)` — empty list when absent
  - `Task<T?> JobStore.ReadArtifactAsync<T>(string jobId, string fileName, CancellationToken ct)` — null when absent or corrupt
  - `Task<byte[]?> JobStore.ReadArtifactBytesAsync(string jobId, string fileName, CancellationToken ct)` — null when absent
  - All four take the same per-job lock as `SaveAsync`/`LoadAsync`, and writes go through temp-file + `File.Move(overwrite: true)`.

- [ ] **Step 1: Write the failing test** — `tests/PoMode.Integration/JobStoreArtifactTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

public sealed class JobStoreArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-artifact-{Guid.NewGuid():N}");

    private JobStore Store => new(
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
        TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<string> NewJobAsync(JobStore store)
    {
        using var content = new MemoryStream(TestAudio.MakeWav());
        return (await store.CreateAsync("song.wav", content, CancellationToken.None)).JobId;
    }

    [Fact]
    public async Task Artifacts_round_trip_through_the_store()
    {
        var store = Store;
        var jobId = await NewJobAsync(store);
        List<NoteEvent> notes = [new(60, 0, 0.5, 96)];

        await store.WriteArtifactAsync(jobId, "notes.json", notes, CancellationToken.None);
        var back = await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", CancellationToken.None);

        Assert.Single(back);
        Assert.Equal(60, back[0].MidiPitch);
    }

    [Fact]
    public async Task Missing_artifact_reads_as_empty_or_null_not_a_throw()
    {
        var store = Store;
        var jobId = await NewJobAsync(store);

        Assert.Empty(await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", CancellationToken.None));
        Assert.Null(await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", CancellationToken.None));
        Assert.Null(await store.ReadArtifactBytesAsync(jobId, "vocals.wav", CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_artifact_reads_as_null_not_a_throw()
    {
        var store = Store;
        var jobId = await NewJobAsync(store);
        await File.WriteAllTextAsync(Path.Combine(store.JobDir(jobId), "result.json"), "{ not json");

        Assert.Null(await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_reads_and_writes_of_one_artifact_never_throw()
    {
        var store = Store;
        var jobId = await NewJobAsync(store);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 60 && !cts.IsCancellationRequested; i++)
            {
                List<NoteEvent> notes = [.. Enumerable.Range(0, 200).Select(n => new NoteEvent(60 + (n % 12), n * 0.1, 0.4, 96))];
                await store.WriteArtifactAsync(jobId, "notes.json", notes, cts.Token);
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 60 && !cts.IsCancellationRequested; i++)
            {
                _ = await store.ReadArtifactBytesAsync(jobId, "notes.json", cts.Token);
                _ = await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", cts.Token);
            }
        })).ToArray();

        // The whole point: no UnauthorizedAccessException / IOException escapes.
        await Task.WhenAll([writer, .. readers]);
    }
}
```

- [ ] **Step 2: RED** — `dotnet test tests/PoMode.Integration --filter JobStoreArtifactTests` (tee `task-1-red.log`). Expect compile errors (methods don't exist).

- [ ] **Step 3: Implement in `JobStore.cs`** — add beside the existing lock-guarded `SaveAsync`/`LoadAsync`, reusing `LockFor(jobId)` and the same temp-file + `File.Move` pattern:

```csharp
public async Task WriteArtifactAsync<T>(string jobId, string fileName, T payload, CancellationToken ct)
{
    var gate = LockFor(jobId);
    await gate.WaitAsync(ct);
    try
    {
        var path = Path.Combine(JobDir(jobId), fileName);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload, JsonOptions), ct);
        File.Move(tempPath, path, overwrite: true);
    }
    finally
    {
        gate.Release();
    }
}

public async Task<IReadOnlyList<T>> ReadArtifactListAsync<T>(string jobId, string fileName, CancellationToken ct)
    => await ReadArtifactAsync<List<T>>(jobId, fileName, ct) ?? [];

public async Task<T?> ReadArtifactAsync<T>(string jobId, string fileName, CancellationToken ct)
{
    var gate = LockFor(jobId);
    await gate.WaitAsync(ct);
    try
    {
        var path = Path.Combine(JobDir(jobId), fileName);
        if (!File.Exists(path))
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), JsonOptions);
    }
    catch (JsonException)
    {
        return default; // a torn or corrupt artifact is "not available", never a 500
    }
    finally
    {
        gate.Release();
    }
}

/// <summary>Reads an artifact as bytes under the per-job lock so endpoints never stream a file mid-write.</summary>
public async Task<byte[]?> ReadArtifactBytesAsync(string jobId, string fileName, CancellationToken ct)
{
    var gate = LockFor(jobId);
    await gate.WaitAsync(ct);
    try
    {
        var path = Path.Combine(JobDir(jobId), fileName);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }
    finally
    {
        gate.Release();
    }
}
```

- [ ] **Step 4: Route every artifact access through the store**

- `AnalysisPipeline.WriteArtifactAsync` — delete the private helper; call `store.WriteArtifactAsync(jobId, "notes.json", notes, ct)` / `"chords.json"`.
- `ArtifactModalAnalyzer` — take `JobStore` via constructor injection; replace its private `ReadAsync<T>` with `store.ReadArtifactListAsync<T>`, and write via `store.WriteArtifactAsync(context.JobId, "result.json", result, ct)`. (It is registered as a singleton; `JobStore` is a singleton too, so injection is safe.)
- `AnalysisEndpoints.MapArtifact` — replace `TypedResults.PhysicalFile(path, ...)` with a bytes read:

```csharp
private static void MapArtifact(RouteGroupBuilder group, string route, string fileName)
    => group.MapGet($"/{{jobId}}/{route}", async Task<Results<FileContentHttpResult, NotFound>> (
        string jobId, JobStore store, CancellationToken ct) =>
    {
        if (!IsValidJobId(jobId))
        {
            return TypedResults.NotFound();
        }
        var bytes = await store.ReadArtifactBytesAsync(jobId, fileName, ct);
        return bytes is null
            ? TypedResults.NotFound()
            : TypedResults.File(bytes, "application/json");
    });
```

- `MidiExportEndpoints` — read `result.json`, `notes.json`, `chords.json` through the store's methods instead of direct `File` calls (this also replaces its local `try/catch (JsonException)`, which the store now owns).

- [ ] **Step 5: GREEN** — `dotnet test` (tee `task-1-green.log`). Expect 117 passed (113 + 4 new), zero warnings. Because this touches the artifact path used by the browser test, ALSO run `dotnet test tests/PoMode.E2EUI` alone and report both.

- [ ] **Step 6: Commit**

```powershell
git add src/PoMode.API tests/PoMode.Integration/JobStoreArtifactTests.cs
git commit -m "fix: serialise all artifact reads and writes through the per-job lock"
```

---

### Task 2: Stem-Model Feasibility Gate (go/no-go)

**Files:**
- Create: `.superpowers/spikes/2026-08-16-stem-model-feasibility.md` (report only — git-ignored)
- No production code in this task.

**This task's deliverable is a DECISION, not code.** Timebox it. A clear "no usable model" is a valid, valuable result.

**Interfaces:**
- Consumes: the spike harness approach from `.superpowers/spikes/2026-08-16-onnx-arm64-spike.md`.
- Produces: the model URL + SHA-256 + input/output tensor contract that Task 8 will hard-code, or a documented no-go.

- [ ] **Step 1: Find a candidate ONNX stem-separation model.** In the scratchpad (never the repo), search for and attempt to download, in preference order:
  1. A Mel-Band Roformer vocal-separation ONNX export.
  2. An HTDemucs v4 ONNX export.
  3. `spleeter`-class 2-stem ONNX, or any published vocals/instrumental ONNX.
  Record for each: URL tried, HTTP result, file size, SHA-256.

- [ ] **Step 2: Inspect the model contract.** For whatever downloaded, load it with `Microsoft.ML.OnnxRuntime` and print every input and output: name, element type, and dimensions (noting which are dynamic). Write these down exactly — Task 8 depends on them.

- [ ] **Step 3: Run it once on real audio.** Feed a short real clip (10–20 s; generate a tone/noise mix if no real file is handy — do NOT use the user's `2017_LonelyHill2.mp3` without need, and never copy it into the repo). Confirm it produces output tensors of the expected shape without throwing.

- [ ] **Step 4: Benchmark honestly.** Time separation for a 30-second clip on the CPU EP, then extrapolate to 3.5 minutes and state peak process memory. The user has explicitly accepted slowness — the job here is an accurate number, not a flattering one.

- [ ] **Step 5: Decide and record.** Write the report with a GO or NO-GO:
  - **GO** requires: a downloadable model with a stable URL, a known SHA-256, a documented tensor contract, and a successful run. Include the extrapolated minutes-per-song.
  - **NO-GO** if no model can be obtained or none runs. Then Task 8 is dropped from this phase; say so plainly and list what was tried.
  - Either way, state whether the model needs STFT/ISTFT done in C# (most Roformer exports take spectrogram input) or accepts raw waveform — this materially changes Task 8's size.

- [ ] **Step 6: Report the decision** in the task report. No commit (the report lives in git-ignored `.superpowers/`).

---

### Task 3: Audio Decoding

**Files:**
- Create: `src/PoMode.API/Features/Audio/AudioDecoder.cs`, `src/PoMode.API/Features/Audio/AudioBuffer.cs`
- Test: `tests/PoMode.Unit/Audio/AudioDecoderTests.cs`
- Modify: `tests/TestCommon/TestAudio.cs` (add a tone generator)

**Interfaces:**
- Consumes: nothing.
- Produces (used by Tasks 5–8):
  - `record AudioBuffer(float[] Samples, int SampleRate, int Channels)` with `double DurationSeconds => Samples.Length / (double)(SampleRate * Channels)`
  - `static AudioBuffer AudioDecoder.Decode(string path)` — WAV and MP3, by content not extension
  - `static AudioBuffer AudioDecoder.ToMono(AudioBuffer)` — averages channels
  - `static AudioBuffer AudioDecoder.Resample(AudioBuffer, int targetSampleRate)` — linear interpolation, mono only (throws for multi-channel: callers mono-ise first)

- [ ] **Step 1: Add packages**

```powershell
dotnet add src/PoMode.API package NAudio
dotnet add src/PoMode.API package NLayer.NAudioSupport
```

- [ ] **Step 2: Extend the test helper** — add to `tests/TestCommon/TestAudio.cs`:

```csharp
/// <summary>A mono sine tone as a valid PCM16 WAV — real signal for decode and tempo tests.</summary>
public static byte[] MakeTone(double seconds, double frequencyHz, int sampleRate = 22050, double amplitude = 0.5)
{
    var count = (int)(seconds * sampleRate);
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    var dataSize = count * 2;
    writer.Write("RIFF"u8);
    writer.Write(36 + dataSize);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(dataSize);
    for (var i = 0; i < count; i++)
    {
        var value = amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate);
        writer.Write((short)(value * short.MaxValue));
    }
    writer.Flush();
    return stream.ToArray();
}

/// <summary>Clicks at a fixed BPM — ground truth for the tempo estimator.</summary>
public static byte[] MakeClickTrack(double seconds, double bpm, int sampleRate = 22050)
{
    var count = (int)(seconds * sampleRate);
    var samples = new short[count];
    var samplesPerBeat = 60.0 / bpm * sampleRate;
    for (var beat = 0; beat * samplesPerBeat < count; beat++)
    {
        var start = (int)(beat * samplesPerBeat);
        for (var i = 0; i < 200 && start + i < count; i++)
        {
            // Short decaying burst = a sharp onset the envelope can find.
            var envelope = 1.0 - (i / 200.0);
            samples[start + i] = (short)(envelope * 0.8 * short.MaxValue * Math.Sin(2 * Math.PI * 1000 * i / sampleRate));
        }
    }

    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    var dataSize = count * 2;
    writer.Write("RIFF"u8);
    writer.Write(36 + dataSize);
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16);
    writer.Write((short)1);
    writer.Write((short)1);
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2);
    writer.Write((short)2);
    writer.Write((short)16);
    writer.Write("data"u8);
    writer.Write(dataSize);
    foreach (var sample in samples)
    {
        writer.Write(sample);
    }
    writer.Flush();
    return stream.ToArray();
}
```

- [ ] **Step 3: Write the failing tests** — `tests/PoMode.Unit/Audio/AudioDecoderTests.cs`:

```csharp
using PoMode.API.Features.Audio;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Unit.Audio;

public sealed class AudioDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-audio-{Guid.NewGuid():N}");

    public AudioDecoderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteTemp(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Decodes_a_wav_to_the_expected_length_and_rate()
    {
        var path = WriteTemp("tone.wav", TestAudio.MakeTone(seconds: 1.0, frequencyHz: 440, sampleRate: 22050));

        var buffer = AudioDecoder.Decode(path);

        Assert.Equal(22050, buffer.SampleRate);
        Assert.Equal(1, buffer.Channels);
        Assert.InRange(buffer.DurationSeconds, 0.98, 1.02);
    }

    [Fact]
    public void Decoded_samples_are_normalised_floats()
    {
        var path = WriteTemp("tone.wav", TestAudio.MakeTone(1.0, 440, amplitude: 0.5));

        var buffer = AudioDecoder.Decode(path);

        Assert.All(buffer.Samples, s => Assert.InRange(s, -1.0f, 1.0f));
        Assert.InRange(buffer.Samples.Max(), 0.4f, 0.6f); // amplitude survives
    }

    [Fact]
    public void Resampling_halves_the_sample_count_when_halving_the_rate()
    {
        var buffer = AudioDecoder.Decode(WriteTemp("tone.wav", TestAudio.MakeTone(1.0, 440, sampleRate: 44100)));

        var resampled = AudioDecoder.Resample(buffer, 22050);

        Assert.Equal(22050, resampled.SampleRate);
        Assert.InRange(resampled.Samples.Length, 22000, 22100);
        Assert.InRange(resampled.DurationSeconds, 0.98, 1.02);
    }

    [Fact]
    public void Mono_conversion_averages_channels()
    {
        var stereo = new AudioBuffer([1.0f, -1.0f, 0.5f, -0.5f], 8000, 2);

        var mono = AudioDecoder.ToMono(stereo);

        Assert.Equal(1, mono.Channels);
        Assert.Equal([0.0f, 0.0f], mono.Samples);
    }

    [Fact]
    public void Unsupported_content_throws_a_clear_error()
    {
        var path = WriteTemp("junk.wav", [0x25, 0x50, 0x44, 0x46, 0, 0, 0, 0, 0, 0, 0, 0]);

        var ex = Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(path));
        Assert.Contains("audio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: RED** — tee `task-3-red.log`.

- [ ] **Step 5: Implement**

`src/PoMode.API/Features/Audio/AudioBuffer.cs`:
```csharp
namespace PoMode.API.Features.Audio;

/// <summary>Interleaved, normalised (-1..1) PCM.</summary>
public sealed record AudioBuffer(float[] Samples, int SampleRate, int Channels)
{
    public double DurationSeconds => Samples.Length / (double)(SampleRate * Math.Max(Channels, 1));
}
```

`src/PoMode.API/Features/Audio/AudioDecoder.cs`:
```csharp
using NAudio.Wave;
using NLayer.NAudioSupport;
using PoMode.API.Features.Analysis;

namespace PoMode.API.Features.Audio;

/// <summary>Decodes uploads to normalised float PCM. Format is sniffed from content, never the extension.</summary>
public static class AudioDecoder
{
    public static AudioBuffer Decode(string path)
    {
        var header = new byte[12];
        using (var probe = File.OpenRead(path))
        {
            var read = probe.Read(header);
            if (!AudioFormatValidator.IsSupported(header.AsSpan(0, read), out _))
            {
                throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a supported audio file (wav or mp3).");
            }
        }

        using var reader = OpenReader(path, header);
        var provider = reader.ToSampleProvider();
        var format = provider.WaveFormat;

        var buffer = new List<float>(capacity: 1 << 20);
        var chunk = new float[format.SampleRate * format.Channels];
        int count;
        while ((count = provider.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.AddRange(chunk.AsSpan(0, count));
        }

        return new AudioBuffer([.. buffer], format.SampleRate, format.Channels);
    }

    private static WaveStream OpenReader(string path, ReadOnlySpan<byte> header)
        => header[..4].SequenceEqual("RIFF"u8)
            ? new WaveFileReader(path)
            : new Mp3FileReaderBase(path, wave => new Mp3FrameDecompressor(wave));

    public static AudioBuffer ToMono(AudioBuffer buffer)
    {
        if (buffer.Channels <= 1)
        {
            return buffer;
        }

        var frames = buffer.Samples.Length / buffer.Channels;
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < buffer.Channels; channel++)
            {
                sum += buffer.Samples[(frame * buffer.Channels) + channel];
            }
            mono[frame] = sum / buffer.Channels;
        }
        return new AudioBuffer(mono, buffer.SampleRate, 1);
    }

    public static AudioBuffer Resample(AudioBuffer buffer, int targetSampleRate)
    {
        if (buffer.Channels != 1)
        {
            throw new ArgumentException("Resample expects mono input; call ToMono first.", nameof(buffer));
        }
        if (buffer.SampleRate == targetSampleRate || buffer.Samples.Length == 0)
        {
            return buffer with { SampleRate = targetSampleRate };
        }

        var ratio = (double)targetSampleRate / buffer.SampleRate;
        var length = (int)(buffer.Samples.Length * ratio);
        var output = new float[length];
        for (var i = 0; i < length; i++)
        {
            var source = i / ratio;
            var index = (int)source;
            var fraction = (float)(source - index);
            var a = buffer.Samples[Math.Min(index, buffer.Samples.Length - 1)];
            var b = buffer.Samples[Math.Min(index + 1, buffer.Samples.Length - 1)];
            output[i] = a + ((b - a) * fraction);
        }
        return new AudioBuffer(output, targetSampleRate, 1);
    }
}
```

If `Mp3FileReaderBase`/`Mp3FrameDecompressor` differ in the restored NLayer package, use that package's current equivalent and flag the deviation.

- [ ] **Step 6: GREEN** — `dotnet test` (tee `task-3-green.log`). Expect 122 passed. Commit:

```powershell
git add src/PoMode.API/Features/Audio tests/PoMode.Unit/Audio tests/TestCommon/TestAudio.cs Directory.Packages.props src/PoMode.API/PoMode.API.csproj
git commit -m "feat: decode wav and mp3 uploads to normalised float PCM"
```

---

### Task 4: Model Registry

**Files:**
- Create: `src/PoMode.API/Infrastructure/ModelRegistry.cs`, `src/PoMode.API/Infrastructure/ModelDescriptor.cs`
- Modify: `src/PoMode.API/Program.cs` (register), `src/PoMode.API/Features/Hardware/DiagnosticsService.cs` + `src/PoMode.Shared/Hardware/HardwareReport.cs` (report model status), `src/PoMode.Shared/Serialization/PoModeJsonContext.cs`
- Test: `tests/PoMode.Integration/ModelRegistryTests.cs`

**Interfaces:**
- Consumes: `EnvironmentDetector.IsAzureHosted()`.
- Produces (used by Tasks 5 and 8):
  - `record ModelDescriptor(string Key, string FileName, string Url, string Sha256)`
  - `record ModelStatus(string Key, bool Available, long SizeBytes)` (in `PoMode.Shared.Hardware`)
  - `ModelRegistry(IConfiguration, IHttpClientFactory, ILogger<ModelRegistry>)`
    - `string RootPath` — `Models:RootPath` or `<content root>/models`
    - `bool IsDownloaded(ModelDescriptor)`
    - `Task<string> EnsureAsync(ModelDescriptor, CancellationToken)` — returns the local path; downloads to `.part`, verifies SHA-256, then moves into place; throws `InvalidOperationException` on hash mismatch (and deletes the bad file); no-ops when already present; **throws immediately in Azure mode** (spec §5)
    - `IReadOnlyList<ModelStatus> StatusFor(IEnumerable<ModelDescriptor>)`
  - `HardwareReport` gains `IReadOnlyList<ModelStatus> Models` (append as the last positional parameter; fix the construction sites)

- [ ] **Step 1: Write the failing tests** — `tests/PoMode.Integration/ModelRegistryTests.cs`. Serve the fixture over a real loopback HTTP listener so the download path is genuinely exercised:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Infrastructure;
using Xunit;

namespace PoMode.Integration;

public sealed class ModelRegistryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-models-{Guid.NewGuid():N}");
    private readonly byte[] _payload = Encoding.UTF8.GetBytes("pretend-onnx-bytes");
    private HttpListener _listener = null!;
    private string _baseUrl = null!;

    public Task InitializeAsync()
    {
        var port = 5310;
        _baseUrl = $"http://127.0.0.1:{port}/";
        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                await context.Response.OutputStream.WriteAsync(_payload);
                context.Response.Close();
            }
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _listener.Stop();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private string Sha256Hex => Convert.ToHexString(SHA256.HashData(_payload)).ToLowerInvariant();

    private ModelRegistry Registry()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        return new ModelRegistry(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Models:RootPath"] = _root }).Build(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ModelRegistry>.Instance);
    }

    [Fact]
    public async Task Downloads_and_verifies_a_model()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);
        var registry = Registry();

        var path = await registry.EnsureAsync(descriptor, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(_payload, await File.ReadAllBytesAsync(path));
        Assert.True(registry.IsDownloaded(descriptor));
    }

    [Fact]
    public async Task Second_call_does_not_redownload()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);
        var registry = Registry();

        var first = await registry.EnsureAsync(descriptor, CancellationToken.None);
        var stamp = File.GetLastWriteTimeUtc(first);
        var second = await registry.EnsureAsync(descriptor, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second));
    }

    [Fact]
    public async Task Hash_mismatch_throws_and_leaves_no_file_behind()
    {
        var descriptor = new ModelDescriptor("bad", "bad.onnx", _baseUrl + "bad.onnx", new string('a', 64));
        var registry = Registry();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.EnsureAsync(descriptor, CancellationToken.None));

        Assert.False(registry.IsDownloaded(descriptor));
        Assert.Empty(Directory.GetFiles(registry.RootPath, "*.part"));
    }

    [Fact]
    public void Status_reports_availability_and_size()
    {
        var descriptor = new ModelDescriptor("test", "test.onnx", _baseUrl + "test.onnx", Sha256Hex);

        var status = Registry().StatusFor([descriptor]).Single();

        Assert.Equal("test", status.Key);
        Assert.False(status.Available);
        Assert.Equal(0, status.SizeBytes);
    }
}
```

- [ ] **Step 2: RED** — tee `task-4-red.log`.

- [ ] **Step 3: Implement** `ModelDescriptor`, `ModelStatus` and `ModelRegistry` per the interfaces above. Requirements the tests pin: download to `<file>.part`, hash with `SHA256.HashData`, compare case-insensitively, delete the `.part` on mismatch before throwing, `File.Move(part, final, overwrite: true)` on success, and `Directory.CreateDirectory(RootPath)` first. In Azure mode `EnsureAsync` throws `InvalidOperationException("Local models are disabled in Azure mode.")` without touching the network.

- [ ] **Step 4: Surface in `/diag`** — add `IReadOnlyList<ModelStatus> Models` to `HardwareReport`, fill it in `DiagnosticsService` from `ModelRegistry.StatusFor(ModelCatalog.All)` (define a small static `ModelCatalog` holding the descriptors; the stem entry may be a placeholder until Task 8 fills in the real URL/hash from Task 2). Register `ModelRegistry` as a singleton. Fix every `HardwareReport` construction site.

- [ ] **Step 5: GREEN** — `dotnet test` (tee `task-4-green.log`). Expect 126 passed. Commit:

```powershell
git add src/PoMode.API src/PoMode.Shared tests/PoMode.Integration/ModelRegistryTests.cs
git commit -m "feat: model registry with hash-verified downloads reported in /diag"
```

---

### Task 5: Local Basic Pitch Tracker

**Files:**
- Create: `src/PoMode.API/Features/PitchTracking/BasicPitchDecoder.cs`, `src/PoMode.API/Features/PitchTracking/OnnxPitchTracker.cs`
- Modify: `src/PoMode.API/Program.cs` (register alongside the fake)
- Test: `tests/PoMode.Unit/PitchTracking/BasicPitchDecoderTests.cs`, `tests/PoMode.Integration/OnnxPitchTrackerTests.cs`

**The spike proved this is the cheap win: 12.3 ms per 2-second window on the CPU EP.**

**Interfaces:**
- Consumes: `AudioDecoder` (Task 3), `ModelRegistry` (Task 4), `IPitchTracker`/`StageContext` (Phase 2).
- Produces:
  - `static IReadOnlyList<NoteEvent> BasicPitchDecoder.Decode(float[,] onsets, float[,] frames, double framesPerSecond, int minMidi, double onsetThreshold = 0.5, double frameThreshold = 0.3, double minDurationSec = 0.058)` — pure function, fully unit-testable without a model
  - `OnnxPitchTracker : IPitchTracker` — `Tier = Local`, `Name = nameof(OnnxPitchTracker)`, `IsAvailableAsync` true only when the model file is present (never downloads inside the availability check); `TrackAsync` decodes `vocals.wav` → mono 22.05 kHz → windowed inference → `BasicPitchDecoder`

- [ ] **Step 1: Add the ONNX package**

```powershell
dotnet add src/PoMode.API package Microsoft.ML.OnnxRuntime
```
(CPU package ONLY — see the plan-level ruling. Do not add the DirectML or QNN packages.)

- [ ] **Step 2: Write the decoder tests first** — `tests/PoMode.Unit/PitchTracking/BasicPitchDecoderTests.cs`. These use hand-built posterior arrays, so they need no model and stay in the fast suite:

```csharp
using PoMode.API.Features.PitchTracking;
using Xunit;

namespace PoMode.Unit.PitchTracking;

public class BasicPitchDecoderTests
{
    private const double Fps = 100.0; // 10 ms per frame
    private const int MinMidi = 21;

    private static (float[,] Onsets, float[,] Frames) Empty(int frames, int pitches)
        => (new float[frames, pitches], new float[frames, pitches]);

    [Fact]
    public void A_sustained_note_becomes_one_event_with_the_right_pitch_and_duration()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 0.9f;
        for (var f = 10; f < 40; f++)
        {
            frames[f, bin] = 0.8f;
        }

        var notes = BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi);

        var note = Assert.Single(notes);
        Assert.Equal(60, note.MidiPitch);
        Assert.InRange(note.StartSec, 0.09, 0.11);
        Assert.InRange(note.DurationSec, 0.28, 0.32);
    }

    [Fact]
    public void Notes_shorter_than_the_minimum_are_dropped()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 0.9f;
        frames[10, bin] = 0.8f; // a single 10 ms frame, under the 58 ms floor

        Assert.Empty(BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi));
    }

    [Fact]
    public void Weak_onsets_do_not_start_a_note()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 0.2f; // below the 0.5 onset threshold
        for (var f = 10; f < 40; f++)
        {
            frames[f, bin] = 0.8f;
        }

        Assert.Empty(BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi));
    }

    [Fact]
    public void Two_separate_onsets_on_one_pitch_become_two_notes()
    {
        var (onsets, frames) = Empty(200, 88);
        var bin = 62 - MinMidi;
        onsets[10, bin] = 0.9f;
        for (var f = 10; f < 30; f++) frames[f, bin] = 0.8f;
        onsets[60, bin] = 0.9f;
        for (var f = 60; f < 90; f++) frames[f, bin] = 0.8f;

        var notes = BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi);

        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal(62, n.MidiPitch));
        Assert.True(notes[1].StartSec > notes[0].StartSec);
    }

    [Fact]
    public void Velocity_scales_with_frame_energy_and_stays_in_midi_range()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 1.0f;
        for (var f = 10; f < 40; f++) frames[f, bin] = 1.0f;

        var loud = BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi).Single();

        var (onsets2, frames2) = Empty(100, 88);
        onsets2[10, bin] = 0.6f;
        for (var f = 10; f < 40; f++) frames2[f, bin] = 0.35f;
        var soft = BasicPitchDecoder.Decode(onsets2, frames2, Fps, MinMidi).Single();

        Assert.True(loud.Velocity > soft.Velocity);
        Assert.InRange(loud.Velocity, 1, 127);
        Assert.InRange(soft.Velocity, 1, 127);
    }

    [Fact]
    public void Notes_come_back_in_time_order()
    {
        var (onsets, frames) = Empty(300, 88);
        foreach (var (start, pitch) in new[] { (100, 67), (10, 60), (200, 64) })
        {
            var bin = pitch - MinMidi;
            onsets[start, bin] = 0.9f;
            for (var f = start; f < start + 20; f++) frames[f, bin] = 0.8f;
        }

        var notes = BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi);

        Assert.Equal(3, notes.Count);
        Assert.Equal(notes.OrderBy(n => n.StartSec).Select(n => n.MidiPitch), notes.Select(n => n.MidiPitch));
    }
}
```

- [ ] **Step 3: RED** — tee `task-5-red.log`.

- [ ] **Step 4: Implement `BasicPitchDecoder`.** Algorithm: for each pitch bin, walk frames; when `onsets[f, bin] >= onsetThreshold` and no note is open on that bin, open a note at `f`; extend while `frames[f, bin] >= frameThreshold`; close when it drops below (or a new onset fires, or the array ends). Emit only when `duration >= minDurationSec`. `MidiPitch = bin + minMidi`. `Velocity = Math.Clamp((int)(meanFrameEnergy * 127), 1, 127)`. Sort by `StartSec`, then `MidiPitch`.

- [ ] **Step 5: Implement `OnnxPitchTracker`.** It must:
  - `IsAvailableAsync` → `registry.IsDownloaded(ModelCatalog.BasicPitch)` (plus `!EnvironmentDetector.IsAzureHosted()`).
  - `TrackAsync`: `EnsureAsync` the model; decode `Path.Combine(context.JobDir, "vocals.wav")` (fall back to `context.InputPath` if the stem is absent); `ToMono` then `Resample` to 22050; slice into the model's expected window length with hop, run the session per window, stitch the posterior arrays across windows (accounting for overlap by taking the later window's frames), then call `BasicPitchDecoder`.
  - **The exact input name, output names, and window length come from the model** — read them from `session.InputMetadata` / `session.OutputMetadata` rather than hard-coding, log them once, and record them in your report. The spike's report documents what the real model exposed; reconcile with it.
  - Reuse a single `InferenceSession` per call, dispose it, and set `SessionOptions.IntraOpNumThreads` to `Environment.ProcessorCount / 2` to leave room for the web host.

- [ ] **Step 6: Integration test** — `tests/PoMode.Integration/OnnxPitchTrackerTests.cs`, marked `[Trait("Category", "Slow")]`: skip cleanly (`Assert.True(true)` with a logged reason) when the model is not downloaded, otherwise run a 5-second 440 Hz tone (`TestAudio.MakeTone`) through `TrackAsync` and assert it returns at least one note whose pitch is A4 = 69 ± 1. This is the end-to-end proof that the real model produces musically correct output.

- [ ] **Step 7: Register** in `Program.cs`: `builder.Services.AddSingleton<IPitchTracker, OnnxPitchTracker>();` **in addition to** the fake. Both are `Local` tier; `ExecutionPlanner` orders by tier then registration order, so register the ONNX one FIRST so it wins when available, with the fake as the automatic fallback when `IsAvailableAsync` is false.

- [ ] **Step 8: GREEN** — `dotnet test` (tee `task-5-green.log`). Report the actual count and whether the slow test ran or skipped. Commit:

```powershell
git add src/PoMode.API tests/PoMode.Unit/PitchTracking tests/PoMode.Integration/OnnxPitchTrackerTests.cs Directory.Packages.props
git commit -m "feat: local Basic Pitch note tracking on the ONNX CPU provider"
```

---

### Task 6: Tempo Estimator

**Files:**
- Create: `src/PoMode.API/Features/Audio/TempoEstimator.cs`
- Test: `tests/PoMode.Unit/Audio/TempoEstimatorTests.cs`

**Interfaces:**
- Consumes: `AudioBuffer`, `AudioDecoder` (Task 3).
- Produces: `record TempoEstimate(double Bpm, double Confidence)`; `static TempoEstimate TempoEstimator.Estimate(AudioBuffer buffer, double minBpm = 60, double maxBpm = 200)`.

**Algorithm (deterministic, no model):** mono-ise; frame the signal at 512-sample hops over 1024-sample windows; per frame compute energy; build an onset envelope as the half-wave-rectified positive energy difference between consecutive frames; subtract a moving average (100-frame window) to remove drift; autocorrelate the envelope over lags corresponding to `minBpm..maxBpm`; the peak lag gives the BPM. `Confidence = peak / mean(autocorrelation)` normalised into `[0,1]` via `Math.Clamp((ratio - 1) / 2, 0, 1)`. Silence or too-short input ⇒ `(120.0, 0.0)`.

- [ ] **Step 1: Write the failing tests** — `tests/PoMode.Unit/Audio/TempoEstimatorTests.cs`:

```csharp
using PoMode.API.Features.Audio;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Unit.Audio;

public sealed class TempoEstimatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-tempo-{Guid.NewGuid():N}");

    public TempoEstimatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AudioBuffer Click(double bpm, double seconds = 20.0)
    {
        var path = Path.Combine(_dir, $"click-{bpm}.wav");
        File.WriteAllBytes(path, TestAudio.MakeClickTrack(seconds, bpm));
        return AudioDecoder.Decode(path);
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(120.0)]
    [InlineData(140.0)]
    public void Finds_the_tempo_of_a_click_track(double bpm)
    {
        var estimate = TempoEstimator.Estimate(Click(bpm));

        // Octave errors (half/double time) are the classic failure; assert the true tempo,
        // and let the implementation pick the octave inside the min/max band.
        Assert.InRange(estimate.Bpm, bpm - 3, bpm + 3);
        Assert.True(estimate.Confidence > 0);
    }

    [Fact]
    public void Silence_falls_back_to_120_with_zero_confidence()
    {
        var estimate = TempoEstimator.Estimate(new AudioBuffer(new float[22050 * 5], 22050, 1));

        Assert.Equal(120.0, estimate.Bpm);
        Assert.Equal(0.0, estimate.Confidence);
    }

    [Fact]
    public void Very_short_input_falls_back_rather_than_throwing()
    {
        var estimate = TempoEstimator.Estimate(new AudioBuffer(new float[512], 22050, 1));

        Assert.Equal(120.0, estimate.Bpm);
        Assert.Equal(0.0, estimate.Confidence);
    }

    [Fact]
    public void Estimates_stay_inside_the_requested_band()
    {
        var estimate = TempoEstimator.Estimate(Click(120.0), minBpm: 100, maxBpm: 130);

        Assert.InRange(estimate.Bpm, 100, 130);
    }
}
```

- [ ] **Step 2: RED** — tee `task-6-red.log`.

- [ ] **Step 3: Implement** per the algorithm above. If a click track lands consistently on double or half tempo, that is an octave error: prefer the lag whose autocorrelation peak is highest *after* weighting toward the middle of the band (a common, principled fix) — do not special-case the test values.

- [ ] **Step 4: GREEN** — `dotnet test` (tee `task-6-green.log`). Expect 133 passed. Commit:

```powershell
git add src/PoMode.API/Features/Audio/TempoEstimator.cs tests/PoMode.Unit/Audio/TempoEstimatorTests.cs
git commit -m "feat: onset-envelope tempo estimation with click-track tests"
```

---

### Task 7: Real Tempo Through the Pipeline

**Files:**
- Modify: `src/PoMode.API/Features/ModalAnalysis/ModalAnalysisEngine.cs` (accept `tempoEstimated`), `src/PoMode.API/Features/ModalAnalysis/ArtifactModalAnalyzer.cs` (estimate and pass the real BPM), `src/PoMode.Client/Components/ModalResultView.razor` (already derives the label — verify)
- Test: `tests/PoMode.Unit/ModalAnalysis/ModalAnalysisEngineTests.cs` (extend), `tests/PoMode.Integration/ArtifactModalAnalyzerTests.cs` (extend)

**This closes the Phase 3 loan:** `ModalResult.TempoEstimated` becomes `false` when a real estimate was used, and the UI's "(estimated)" suffix disappears on its own.

**Interfaces:**
- Consumes: `TempoEstimator` (Task 6), `AudioDecoder` (Task 3).
- Produces: `ModalAnalysisEngine.Analyze(notes, chords, tempoBpm = 120.0, tempoEstimated = true)`; `ArtifactModalAnalyzer` decodes the instrumental (or input) audio, estimates tempo, and passes `(bpm, estimated: false)` when confidence > 0.

- [ ] **Step 1: Extend the engine tests**

```csharp
[Fact]
public void Real_tempo_is_reported_as_not_estimated()
{
    var result = ModalAnalysisEngine.Analyze([], [], tempoBpm: 96.0, tempoEstimated: false);

    Assert.Equal(96.0, result.TempoBpm);
    Assert.False(result.TempoEstimated);
}

[Fact]
public void Default_still_reports_an_estimated_120()
{
    var result = ModalAnalysisEngine.Analyze([], []);

    Assert.Equal(120.0, result.TempoBpm);
    Assert.True(result.TempoEstimated);
}
```

- [ ] **Step 2: Extend the analyzer test** — assert that when `instrumental.wav` in the job folder is a 120 BPM click track, the written `result.json` has `TempoEstimated == false` and a `TempoBpm` within ±3 of 120.

- [ ] **Step 3: RED → implement → GREEN** (tee `task-7-{red,green}.log`). In `ArtifactModalAnalyzer`: pick `instrumental.wav` if present else the job's input file; decode; `TempoEstimator.Estimate`; if `Confidence > 0` pass `(estimate.Bpm, tempoEstimated: false)` else `(120.0, true)`. Wrap decode in a try/catch so an undecodable stem degrades to the 120 default rather than failing the job — and log it.

- [ ] **Step 4: Commit**

```powershell
git add src/PoMode.API tests/PoMode.Unit/ModalAnalysis tests/PoMode.Integration/ArtifactModalAnalyzerTests.cs
git commit -m "feat: derive the real tempo and stop labelling it estimated"
```

---

### Task 8: Local Stem Separation (gated on Task 2)

**Run this task ONLY if Task 2 returned GO.** If Task 2 was NO-GO, skip it, record that in the ledger, and go straight to the phase exit checks.

**Files:**
- Create: `src/PoMode.API/Features/StemSeparation/OnnxStemSeparator.cs`, plus an STFT/ISTFT helper under `src/PoMode.API/Features/Audio/` **only if** Task 2 reported the model needs spectrogram input
- Create: `src/PoMode.API/Features/Audio/WavWriter.cs` (write an `AudioBuffer` back to a PCM16 WAV — the stems must be real files the next stage can decode)
- Modify: `src/PoMode.API/Program.cs`, `ModelCatalog`
- Test: `tests/PoMode.Unit/Audio/WavWriterTests.cs`, `tests/PoMode.Integration/OnnxStemSeparatorTests.cs`

**Interfaces:**
- Consumes: Task 2's model URL + SHA-256 + tensor contract; `ModelRegistry`; `AudioDecoder`.
- Produces: `OnnxStemSeparator : IStemSeparator` — `Tier = Local`, available only when the model is downloaded; writes `vocals.wav` and `instrumental.wav` into `context.JobDir`.

- [ ] **Step 1: `WavWriter` first (round-trip tested).** `static void WavWriter.Write(string path, AudioBuffer buffer)` producing PCM16. Test: write a tone, decode it back with `AudioDecoder`, assert sample rate, channel count, duration, and that peak amplitude survives within 1%.

- [ ] **Step 2: Add the real descriptor** to `ModelCatalog` using Task 2's exact URL and SHA-256.

- [ ] **Step 3: Implement `OnnxStemSeparator`** against the tensor contract Task 2 documented: decode input → the model's expected rate/channels → chunk with overlap → run → overlap-add reconstruct → write `vocals.wav`; write `instrumental.wav` as either the model's second output or `mix − vocals` (sample-wise), whichever the contract supports. Chunk size and overlap must come from measured memory headroom — this box has 15.6 GB total.

- [ ] **Step 4: Integration test** `[Trait("Category", "Slow")]`: skip when the model is absent; otherwise separate a 10-second mixed tone (two tones at different frequencies) and assert both stems exist, are decodable, and have the same duration as the input (±5%). Do **not** assert separation quality — that is not testable deterministically.

- [ ] **Step 5: Register FIRST among `IStemSeparator`s** so it beats the fake when the model is present.

- [ ] **Step 6: Measure and report honestly.** Time a 30-second clip end to end, extrapolate to 3.5 minutes, and put both numbers in the report and the ledger. The user accepted slowness knowing the risk; they are owed the real figure.

- [ ] **Step 7: GREEN + commit**

```powershell
git add src/PoMode.API tests/PoMode.Unit/Audio/WavWriterTests.cs tests/PoMode.Integration/OnnxStemSeparatorTests.cs
git commit -m "feat: local ONNX stem separation writing real vocal and instrumental stems"
```

---

## Phase 4 Exit Criteria

- `dotnet test` green across all four projects (E2EUI re-checked alone if the full run starves it); zero build warnings; no `Version=` in any csproj; no secrets; `models/` and `.superpowers/` ignored.
- The artifact race is closed: concurrent read/write test passes, and no endpoint streams a file in place.
- Manual: `dotnet run --project src/PoMode.API` → upload a real song → `/diag` lists model status → the analysis uses real Basic Pitch notes and a real BPM, and the UI no longer says "(estimated)".
- The measured stem-separation time for a 3.5-minute track is recorded in the ledger and reported to the user, whatever it is.
- Phase 5 starts from: chord recognition (BTC export, the remaining fake), the cloud tier, and WebGPU client delegation.
