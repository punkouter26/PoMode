# PoMode Phase 5: Chord Recognition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `FakeChordRecognizer` — the last fake executor — with deterministic chroma-based chord recognition in pure C#, so an uploaded song produces its own chord progression, and the "USING MOCK DATA" banner finally switches off for a fully-real analysis.

**Architecture:** A new `Features/ChordRecognition/` DSP chain, all pure functions over `AudioBuffer`: STFT → log-frequency spectrum → 12-bin chroma per frame → template match against chord templates → temporal smoothing → merge equal neighbours into `ChordSpan`s. `ChromaChordRecognizer` implements the existing Phase-2 `IChordRecognizer` seam and reports `IsAvailableAsync = true` unconditionally (no model, no network), so `ExecutionPlanner` prefers it over the fake automatically.

**Tech Stack:** .NET 10, `System.Numerics.Complex` for the FFT (no new packages), xUnit, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-17-pomode-design.md` — actually `docs/superpowers/specs/2026-08-16-pomode-design.md` §5 (Stage 3 chords), §13.

**Plan-level rulings (decided now):**
- **User decision: chroma template matching, not BTC.** The spec's §5 lists "NNLS-Chroma/Chordino" alongside BTC, so this is spec-sanctioned, not a downgrade. BTC's PyTorch→ONNX export — flagged as the project's top risk since §11 — is **not attempted in this phase**.
- **No model, no download, no network.** This stage must work on a fresh clone with no internet. That is the whole point of choosing it.
- **Chord vocabulary: 24 triads (12 major + 12 minor) plus "N" (no chord).** Sevenths are deliberately excluded: with 12-bin chroma they are frequently indistinguishable from their relative triads, and a confidently-wrong `Cmaj7` is worse for the modal engine than a correct `C`. Revisit only with evidence.
- **Deterministic and fully unit-testable.** Every stage is a pure function; the tests synthesise audio with known chords and assert the recognised symbols. No "run it and eyeball it".
- **Beat-synchronous segmentation is NOT in this phase.** Chords are recognised on a fixed frame grid and merged; aligning boundaries to the Phase-4 beat grid is a later refinement.
- Existing `ChordSpan(Symbol, Root, Quality, StartSec, EndSec)` is unchanged — `Quality` stays `"maj"`/`"min"` so `MidiFileBuilder`'s existing voicing map keeps working untouched.

## Global Constraints

- All prior constraints hold: `net10.0`, Nullable + TreatWarningsAsErrors from `Directory.Build.props` only; CPM (versions ONLY in `Directory.Packages.props`); `PoMode.` prefixes; no secrets; endpoints via `MapGroup()` + `TypedResults`; zero inline CSS.
- **TDD with log-file evidence is mandatory.** Tee every RED and GREEN run to `<workspace>/task-N-{red,green}.log` and quote them. **Check the full log yourself before reporting DONE** — four `Passed!` lines with `Failed: 0`.
- **Known environment quirk:** a full-solution run can starve `PoMode.E2EUI` on this box. If E2EUI fails only inside a full run, re-run `dotnet test tests/PoMode.E2EUI` alone and report both results. Never add sleeps.
- **The app may be running on port 5000** holding a lock on the API build output. If a build fails with a file-in-use error on `PoMode.API.dll`, kill the process on port 5000 (`netstat -ano | grep :5000`, then `taskkill //PID <pid> //F`) and say so. Never touch other ports.
- Commit hygiene: stage only each task's listed paths. Never stage `.claude/`, `.superpowers/`, `models/`, `*.mp3`, `*.wav`, `*.onnx`.
- Commits: conventional style ending with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- Run all commands from repo root `c:\Users\punko\Downloads\PoMode`.

---

### Task 1: FFT and Chroma Extraction

**Files:**
- Create: `src/PoMode.API/Features/ChordRecognition/Fft.cs`, `src/PoMode.API/Features/ChordRecognition/ChromaExtractor.cs`
- Modify: `tests/TestCommon/TestAudio.cs` (add a chord-tone generator)
- Test: `tests/PoMode.Unit/ChordRecognition/FftTests.cs`, `tests/PoMode.Unit/ChordRecognition/ChromaExtractorTests.cs`

**Interfaces:**
- Consumes: `AudioBuffer`, `AudioDecoder` (Phase 4).
- Produces (consumed by Tasks 2–3):
  - `static void Fft.Transform(Complex[] buffer)` — in-place radix-2 Cooley-Tukey; length must be a power of two (throws `ArgumentException` otherwise)
  - `static float[] ChromaExtractor.Frame(ReadOnlySpan<float> samples, int sampleRate)` — one 12-bin chroma vector, L2-normalised (all-zero input returns all zeros, never NaN)
  - `static ChromaGram ChromaExtractor.Compute(AudioBuffer buffer, int windowSize = 4096, int hopSize = 2048)` where `record ChromaGram(float[][] Frames, double FramesPerSecond)`
  - `TestAudio.MakeChord(double seconds, int[] midiPitches, int sampleRate = 22050)` — sums sine tones (plus a quieter octave partial per note so it is not a pure sine) into a valid PCM16 WAV

**Chroma algorithm:** Hann-window each frame → FFT → magnitude spectrum → for each bin above ~55 Hz and below Nyquist, map frequency to a pitch class via `69 + 12*log2(f/440)` rounded, `mod 12`, and accumulate magnitude → L2-normalise the 12-vector.

- [ ] **Step 1: Add the chord tone generator** to `tests/TestCommon/TestAudio.cs`:

```csharp
/// <summary>Sums sine tones (each with a quieter octave partial) into a PCM16 WAV — synthetic "chord" audio.</summary>
public static byte[] MakeChord(double seconds, int[] midiPitches, int sampleRate = 22050)
{
    var count = (int)(seconds * sampleRate);
    var samples = new double[count];
    foreach (var midi in midiPitches)
    {
        var frequency = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)sampleRate;
            samples[i] += Math.Sin(2 * Math.PI * frequency * t)
                + (0.35 * Math.Sin(2 * Math.PI * frequency * 2 * t));
        }
    }

    var peak = samples.Length == 0 ? 1.0 : Math.Max(samples.Max(Math.Abs), 1e-9);
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
        writer.Write((short)(sample / peak * 0.8 * short.MaxValue));
    }
    writer.Flush();
    return stream.ToArray();
}

/// <summary>MIDI pitches for a root-position triad in octave 3. Quality: "maj" or "min".</summary>
public static int[] Triad(int rootPitchClass, string quality)
{
    var root = 48 + rootPitchClass; // C3 = 48
    var third = quality == "min" ? root + 3 : root + 4;
    return [root, third, root + 7];
}
```

- [ ] **Step 2: Write the failing FFT tests** — `tests/PoMode.Unit/ChordRecognition/FftTests.cs`:

```csharp
using System.Numerics;
using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class FftTests
{
    [Fact]
    public void A_pure_tone_peaks_in_the_expected_bin()
    {
        const int size = 1024;
        const int sampleRate = 8000;
        const double frequency = 1000.0; // bin = 1000 / (8000/1024) = 128
        var buffer = new Complex[size];
        for (var i = 0; i < size; i++)
        {
            buffer[i] = new Complex(Math.Sin(2 * Math.PI * frequency * i / sampleRate), 0);
        }

        Fft.Transform(buffer);

        var peak = 0;
        for (var i = 1; i < size / 2; i++)
        {
            if (buffer[i].Magnitude > buffer[peak].Magnitude) peak = i;
        }
        Assert.InRange(peak, 126, 130);
    }

    [Fact]
    public void A_constant_signal_has_all_its_energy_at_dc()
    {
        var buffer = Enumerable.Repeat(new Complex(1, 0), 256).ToArray();

        Fft.Transform(buffer);

        Assert.Equal(256.0, buffer[0].Magnitude, precision: 3);
        for (var i = 1; i < 256; i++)
        {
            Assert.True(buffer[i].Magnitude < 1e-6, $"bin {i} had {buffer[i].Magnitude}");
        }
    }

    [Fact]
    public void Non_power_of_two_lengths_are_rejected()
        => Assert.Throws<ArgumentException>(() => Fft.Transform(new Complex[100]));
}
```

- [ ] **Step 3: Write the failing chroma tests** — `tests/PoMode.Unit/ChordRecognition/ChromaExtractorTests.cs`:

```csharp
using PoMode.API.Features.Audio;
using PoMode.API.Features.ChordRecognition;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public sealed class ChromaExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-chroma-{Guid.NewGuid():N}");

    public ChromaExtractorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AudioBuffer Chord(int[] pitches, double seconds = 2.0)
    {
        var path = Path.Combine(_dir, $"chord-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, TestAudio.MakeChord(seconds, pitches));
        return AudioDecoder.Decode(path);
    }

    [Theory]
    [InlineData(0)]  // C
    [InlineData(5)]  // F
    [InlineData(9)]  // A
    public void A_single_tone_puts_its_energy_in_its_own_pitch_class(int pitchClass)
    {
        var buffer = Chord([48 + pitchClass]);

        var chroma = ChromaExtractor.Compute(buffer).Frames[5];

        var strongest = Array.IndexOf(chroma, chroma.Max());
        Assert.Equal(pitchClass, strongest);
    }

    [Fact]
    public void A_c_major_triad_lights_up_c_e_and_g()
    {
        var buffer = Chord(TestAudio.Triad(0, "maj")); // C E G

        var chroma = ChromaExtractor.Compute(buffer).Frames[5];

        var topThree = chroma
            .Select((value, index) => (value, index))
            .OrderByDescending(pair => pair.value)
            .Take(3)
            .Select(pair => pair.index)
            .Order()
            .ToArray();
        Assert.Equal([0, 4, 7], topThree);
    }

    [Fact]
    public void Chroma_vectors_are_normalised_and_finite()
    {
        var chroma = ChromaExtractor.Compute(Chord(TestAudio.Triad(7, "min"))).Frames[5];

        Assert.All(chroma, value => Assert.True(float.IsFinite(value) && value >= 0));
        var magnitude = Math.Sqrt(chroma.Sum(v => v * v));
        Assert.InRange(magnitude, 0.99, 1.01);
    }

    [Fact]
    public void Silence_yields_a_zero_vector_not_nan()
    {
        var chroma = ChromaExtractor.Frame(new float[4096], 22050);

        Assert.All(chroma, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Frame_rate_matches_the_hop_size()
    {
        var gram = ChromaExtractor.Compute(Chord(TestAudio.Triad(0, "maj"), seconds: 4.0), windowSize: 4096, hopSize: 2048);

        Assert.InRange(gram.FramesPerSecond, 22050 / 2048.0 - 0.1, 22050 / 2048.0 + 0.1);
        Assert.InRange(gram.Frames.Length, 38, 44); // ~4 s at ~10.8 fps
    }
}
```

- [ ] **Step 4: RED** — `dotnet test tests/PoMode.Unit --filter "FftTests|ChromaExtractorTests"` (tee `task-1-red.log`).

- [ ] **Step 5: Implement** `Fft` (iterative in-place radix-2, bit-reversal permutation then butterflies) and `ChromaExtractor` per the algorithm above. `Compute` mono-ises via `AudioDecoder.ToMono` first. Frames are taken at `hopSize` intervals; a final partial frame is zero-padded. Guard: `windowSize` must be a power of two.

- [ ] **Step 6: GREEN** — `dotnet test` (tee `task-1-green.log`). Report the actual count. Commit:

```powershell
git add src/PoMode.API/Features/ChordRecognition tests/PoMode.Unit/ChordRecognition tests/TestCommon/TestAudio.cs
git commit -m "feat: FFT and chroma extraction for chord recognition"
```

---

### Task 2: Chord Templates and Frame Matching

**Files:**
- Create: `src/PoMode.API/Features/ChordRecognition/ChordTemplates.cs`, `src/PoMode.API/Features/ChordRecognition/ChordMatcher.cs`
- Test: `tests/PoMode.Unit/ChordRecognition/ChordTemplatesTests.cs`, `tests/PoMode.Unit/ChordRecognition/ChordMatcherTests.cs`

**Interfaces:**
- Consumes: `ChromaExtractor` output (Task 1), `PitchNames` (Phase 3).
- Produces (consumed by Task 3):
  - `record ChordCandidate(string Symbol, string Root, string Quality, int RootPitchClass)`
  - `ChordTemplates.All` → `IReadOnlyList<(ChordCandidate Chord, float[] Template)>` — 24 entries (12 major, 12 minor), each an L2-normalised 12-vector built by rotating a base template. **Templates are derived by rotation, never hand-written per key** (same discipline as Phase 3's mode masks).
  - `ChordTemplates.NoChord` → the `ChordCandidate` for `"N"`
  - `static (ChordCandidate Chord, double Score) ChordMatcher.Match(float[] chroma, double noChordThreshold = 0.55)` — cosine similarity against every template; the best wins; below the threshold (or an all-zero chroma) returns `NoChord` with score 0

**Symbol format:** major = root name alone (`"C"`, `"F#"`); minor = root name + `"m"` (`"Am"`, `"C#m"`). `Root` is the pitch-class name from `PitchNames.Name`, `Quality` is `"maj"` or `"min"` — matching what `MidiFileBuilder`'s voicing map already understands.

- [ ] **Step 1: Write the failing template tests** — `tests/PoMode.Unit/ChordRecognition/ChordTemplatesTests.cs`:

```csharp
using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordTemplatesTests
{
    [Fact]
    public void There_are_twelve_major_and_twelve_minor_templates()
    {
        Assert.Equal(24, ChordTemplates.All.Count);
        Assert.Equal(12, ChordTemplates.All.Count(entry => entry.Chord.Quality == "maj"));
        Assert.Equal(12, ChordTemplates.All.Count(entry => entry.Chord.Quality == "min"));
    }

    [Fact]
    public void C_major_template_has_energy_only_on_c_e_and_g()
    {
        var template = ChordTemplates.All.Single(e => e.Chord.Symbol == "C").Template;

        var nonZero = template.Select((v, i) => (v, i)).Where(p => p.v > 0).Select(p => p.i).Order().ToArray();
        Assert.Equal([0, 4, 7], nonZero);
    }

    [Fact]
    public void A_minor_template_has_energy_only_on_a_c_and_e()
    {
        var template = ChordTemplates.All.Single(e => e.Chord.Symbol == "Am").Template;

        var nonZero = template.Select((v, i) => (v, i)).Where(p => p.v > 0).Select(p => p.i).Order().ToArray();
        Assert.Equal([0, 4, 9], nonZero);
    }

    [Fact]
    public void Every_template_is_unit_length()
    {
        foreach (var (chord, template) in ChordTemplates.All)
        {
            var magnitude = Math.Sqrt(template.Sum(v => v * v));
            Assert.InRange(magnitude, 0.99, 1.01);
        }
    }

    [Fact]
    public void Symbols_and_roots_are_well_formed()
    {
        Assert.Contains(ChordTemplates.All, e => e.Chord.Symbol == "F#" && e.Chord.Root == "F#" && e.Chord.Quality == "maj");
        Assert.Contains(ChordTemplates.All, e => e.Chord.Symbol == "C#m" && e.Chord.Root == "C#" && e.Chord.Quality == "min");
        Assert.Equal(24, ChordTemplates.All.Select(e => e.Chord.Symbol).Distinct().Count());
    }

    [Fact]
    public void No_chord_is_its_own_candidate()
        => Assert.Equal("N", ChordTemplates.NoChord.Symbol);
}
```

- [ ] **Step 2: Write the failing matcher tests** — `tests/PoMode.Unit/ChordRecognition/ChordMatcherTests.cs`:

```csharp
using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordMatcherTests
{
    private static float[] Chroma(params int[] pitchClasses)
    {
        var chroma = new float[12];
        foreach (var pitchClass in pitchClasses)
        {
            chroma[pitchClass] = 1f;
        }
        var magnitude = (float)Math.Sqrt(chroma.Sum(v => v * v));
        return magnitude == 0 ? chroma : [.. chroma.Select(v => v / magnitude)];
    }

    [Theory]
    [InlineData(new[] { 0, 4, 7 }, "C")]
    [InlineData(new[] { 9, 0, 4 }, "Am")]
    [InlineData(new[] { 7, 11, 2 }, "G")]
    [InlineData(new[] { 2, 5, 9 }, "Dm")]
    [InlineData(new[] { 6, 10, 1 }, "F#")]
    public void A_clean_triad_matches_its_own_chord(int[] pitchClasses, string expected)
    {
        var (chord, score) = ChordMatcher.Match(Chroma(pitchClasses));

        Assert.Equal(expected, chord.Symbol);
        Assert.True(score > 0.9, $"score was {score}");
    }

    [Fact]
    public void A_triad_with_one_extra_note_still_matches()
    {
        // C E G plus a passing D — should still be C, just less confidently.
        var (chord, score) = ChordMatcher.Match(Chroma(0, 4, 7, 2));

        Assert.Equal("C", chord.Symbol);
        Assert.True(score > 0.55);
    }

    [Fact]
    public void Noise_across_all_twelve_classes_is_no_chord()
    {
        var (chord, score) = ChordMatcher.Match(Chroma(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11));

        Assert.Equal("N", chord.Symbol);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Silence_is_no_chord()
    {
        var (chord, _) = ChordMatcher.Match(new float[12]);

        Assert.Equal("N", chord.Symbol);
    }

    [Fact]
    public void Major_and_minor_are_distinguished_by_the_third()
    {
        Assert.Equal("C", ChordMatcher.Match(Chroma(0, 4, 7)).Chord.Symbol);
        Assert.Equal("Cm", ChordMatcher.Match(Chroma(0, 3, 7)).Chord.Symbol);
    }
}
```

- [ ] **Step 3: RED** — tee `task-2-red.log`.

- [ ] **Step 4: Implement.** `ChordTemplates`: base major `{0,4,7}` and minor `{0,3,7}` as 12-vectors, each rotated to all 12 roots and L2-normalised; symbol/root from `PitchNames.Name(rootPitchClass)`. `ChordMatcher.Match`: cosine similarity (both vectors are unit length, so a dot product suffices — but guard against a non-normalised input by dividing by magnitudes); tie-break deterministically by pitch class then major-before-minor.

- [ ] **Step 5: GREEN** — `dotnet test` (tee `task-2-green.log`). Commit:

```powershell
git add src/PoMode.API/Features/ChordRecognition tests/PoMode.Unit/ChordRecognition
git commit -m "feat: rotation-derived chord templates and cosine frame matching"
```

---

### Task 3: Smoothing and Span Merging

**Files:**
- Create: `src/PoMode.API/Features/ChordRecognition/ChordSegmenter.cs`
- Test: `tests/PoMode.Unit/ChordRecognition/ChordSegmenterTests.cs`

**Interfaces:**
- Consumes: `ChordCandidate` + `ChordMatcher` (Task 2).
- Produces (consumed by Task 4): `static IReadOnlyList<ChordSpan> ChordSegmenter.Segment(IReadOnlyList<(ChordCandidate Chord, double Score)> frames, double framesPerSecond, double minDurationSec = 0.5, int medianWindow = 9)`

**Algorithm:** median-filter the per-frame chord labels over `medianWindow` frames (mode of the window, ties broken by the centre frame) to kill single-frame flicker → merge runs of the same symbol into spans → drop spans shorter than `minDurationSec` by absorbing them into the longer neighbour → drop `"N"` spans entirely from the output (they are "no chord", not a chord) → return spans in time order with contiguous `StartSec`/`EndSec`.

- [ ] **Step 1: Write the failing tests** — `tests/PoMode.Unit/ChordRecognition/ChordSegmenterTests.cs`:

```csharp
using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordSegmenterTests
{
    private const double Fps = 10.0;

    private static ChordCandidate Chord(string symbol) =>
        ChordTemplates.All.FirstOrDefault(e => e.Chord.Symbol == symbol).Chord ?? ChordTemplates.NoChord;

    private static List<(ChordCandidate, double)> Frames(params (string Symbol, int Count)[] runs)
    {
        var frames = new List<(ChordCandidate, double)>();
        foreach (var (symbol, count) in runs)
        {
            for (var i = 0; i < count; i++)
            {
                frames.Add((Chord(symbol), symbol == "N" ? 0.0 : 0.9));
            }
        }
        return frames;
    }

    [Fact]
    public void A_steady_run_becomes_one_span()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 40)), Fps);

        var span = Assert.Single(spans);
        Assert.Equal("C", span.Symbol);
        Assert.Equal(0.0, span.StartSec);
        Assert.InRange(span.EndSec, 3.9, 4.1);
    }

    [Fact]
    public void Two_runs_become_two_contiguous_spans()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 30), ("G", 30)), Fps);

        Assert.Equal(2, spans.Count);
        Assert.Equal(["C", "G"], spans.Select(s => s.Symbol).ToArray());
        Assert.Equal(spans[0].EndSec, spans[1].StartSec);
    }

    [Fact]
    public void A_single_flickering_frame_is_smoothed_away()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 20), ("G", 1), ("C", 20)), Fps);

        var span = Assert.Single(spans);
        Assert.Equal("C", span.Symbol);
    }

    [Fact]
    public void Spans_shorter_than_the_minimum_are_absorbed()
    {
        // 2 frames of G = 0.2 s, under the 0.5 s floor.
        var spans = ChordSegmenter.Segment(Frames(("C", 30), ("G", 2), ("C", 30)), Fps, medianWindow: 1);

        Assert.Single(spans);
        Assert.Equal("C", spans[0].Symbol);
    }

    [Fact]
    public void No_chord_regions_are_dropped_from_the_output()
    {
        var spans = ChordSegmenter.Segment(Frames(("N", 30), ("C", 30)), Fps);

        var span = Assert.Single(spans);
        Assert.Equal("C", span.Symbol);
        Assert.InRange(span.StartSec, 2.9, 3.1);
    }

    [Fact]
    public void Root_and_quality_survive_into_the_span()
    {
        var span = ChordSegmenter.Segment(Frames(("Am", 40)), Fps).Single();

        Assert.Equal("Am", span.Symbol);
        Assert.Equal("A", span.Root);
        Assert.Equal("min", span.Quality);
    }

    [Fact]
    public void Empty_input_yields_no_spans()
        => Assert.Empty(ChordSegmenter.Segment([], Fps));
}
```

- [ ] **Step 2: RED** — tee `task-3-red.log`.

- [ ] **Step 3: Implement** per the algorithm. Take care that absorbing a short span merges it into whichever neighbour is longer (and simply extends that neighbour's boundary), and that dropping `"N"` spans does not create overlapping or negative-length spans.

- [ ] **Step 4: GREEN** — `dotnet test` (tee `task-3-green.log`). Commit:

```powershell
git add src/PoMode.API/Features/ChordRecognition tests/PoMode.Unit/ChordRecognition
git commit -m "feat: median smoothing and chord span merging"
```

---

### Task 4: Wire the Real Recogniser into the Pipeline

**Files:**
- Create: `src/PoMode.API/Features/ChordRecognition/ChromaChordRecognizer.cs`
- Modify: `src/PoMode.API/Program.cs`
- Test: `tests/PoMode.Integration/ChromaChordRecognizerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3, `IChordRecognizer`/`StageContext` (Phase 2), `AudioDecoder` (Phase 4).
- Produces: `ChromaChordRecognizer : IChordRecognizer` — `Tier = Local`, `Name = nameof(ChromaChordRecognizer)`, `IsAvailableAsync` returns `true` unconditionally (pure DSP: no model, no network, works in Azure too). `RecognizeAsync` decodes `instrumental.wav` (falling back to `context.InputPath`), computes the chromagram, matches every frame, segments, and returns the spans.

- [ ] **Step 1: Write the failing integration test** — `tests/PoMode.Integration/ChromaChordRecognizerTests.cs`:

```csharp
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Pipeline;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

public sealed class ChromaChordRecognizerTests : IDisposable
{
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), $"pomode-chords-{Guid.NewGuid():N}");

    public ChromaChordRecognizerTests() => Directory.CreateDirectory(_jobDir);

    public void Dispose() => Directory.Delete(_jobDir, recursive: true);

    private StageContext ContextWith(byte[] wav, string fileName = "instrumental.wav")
    {
        File.WriteAllBytes(Path.Combine(_jobDir, fileName), wav);
        return new StageContext("job1", _jobDir, Path.Combine(_jobDir, "input.wav"));
    }

    private static byte[] Progression(params (int Root, string Quality, double Seconds)[] chords)
    {
        // Concatenate per-chord PCM by decoding each and re-encoding is overkill here;
        // build one buffer by summing tone runs sequentially.
        using var stream = new MemoryStream();
        foreach (var (root, quality, seconds) in chords)
        {
            var wav = TestAudio.MakeChord(seconds, TestAudio.Triad(root, quality));
            // Skip the 44-byte header on all but the first, then fix up sizes at the end.
            stream.Write(wav, stream.Length == 0 ? 0 : 44, wav.Length - (stream.Length == 0 ? 0 : 44));
        }
        var bytes = stream.ToArray();
        // Patch RIFF/data sizes so the concatenation is a valid single WAV.
        var dataSize = bytes.Length - 44;
        BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
        return bytes;
    }

    [Fact]
    public async Task Recognises_a_two_chord_progression()
    {
        var context = ContextWith(Progression((0, "maj", 2.0), (9, "min", 2.0))); // C then Am

        var spans = await new ChromaChordRecognizer().RecognizeAsync(context, CancellationToken.None);

        Assert.Equal(2, spans.Count);
        Assert.Equal("C", spans[0].Symbol);
        Assert.Equal("Am", spans[1].Symbol);
        Assert.InRange(spans[1].StartSec, 1.7, 2.3);
    }

    [Fact]
    public async Task Falls_back_to_the_job_input_when_there_is_no_instrumental_stem()
    {
        File.WriteAllBytes(Path.Combine(_jobDir, "input.wav"), TestAudio.MakeChord(3.0, TestAudio.Triad(7, "maj")));
        var context = new StageContext("job1", _jobDir, Path.Combine(_jobDir, "input.wav"));

        var spans = await new ChromaChordRecognizer().RecognizeAsync(context, CancellationToken.None);

        Assert.NotEmpty(spans);
        Assert.Equal("G", spans[0].Symbol);
    }

    [Fact]
    public async Task Silence_yields_no_chords_rather_than_throwing()
    {
        var context = ContextWith(TestAudio.MakeWav(seconds: 2.0));

        var spans = await new ChromaChordRecognizer().RecognizeAsync(context, CancellationToken.None);

        Assert.Empty(spans);
    }

    [Fact]
    public async Task It_is_always_available()
        => Assert.True(await new ChromaChordRecognizer().IsAvailableAsync(CancellationToken.None));
}
```

If the `Progression` helper proves fiddly, replace it with whatever cleanly produces a single WAV containing two consecutive chords — the assertion is what matters, not the construction.

- [ ] **Step 2: RED** — tee `task-4-red.log`.

- [ ] **Step 3: Implement** `ChromaChordRecognizer`, then register it in `Program.cs` **before** `FakeChordRecognizer`:

```csharp
builder.Services.AddSingleton<IChordRecognizer, ChromaChordRecognizer>();
builder.Services.AddSingleton<IChordRecognizer, FakeChordRecognizer>();
```

- [ ] **Step 4: GREEN** — `dotnet test` (tee `task-4-green.log`).

**Expect existing tests to change meaning here — handle honestly:**
- E2E tests asserting `"8 notes · 4 chords"` come from the fakes. With a real recogniser first in line, a synthetic upload will produce a different chord count. Update those assertions to the new truth and explain; do NOT re-order registration to keep the fakes winning.
- The E2EUI banner test (Phase 4) asserts the banner stays VISIBLE because a fake always ran. Once no fake runs, that flips again. Work out what the pipeline genuinely produces and assert that. **This is the moment the banner is supposed to switch off** — if it does, say so prominently in your report.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.API tests/PoMode.Integration tests/PoMode.E2EAPI tests/PoMode.E2EUI
git commit -m "feat: real chroma chord recognition replaces the last fake executor"
```

---

### Task 5: Real-Track Sanity Check and Spec Update

**Files:**
- Modify: `docs/superpowers/specs/2026-08-16-pomode-design.md` (add §13.6)

**Interfaces:**
- Consumes: everything above.
- Produces: an honest, recorded assessment of how the recogniser does on real music, plus the spec correction the Phase-4 final review asked for.

- [ ] **Step 1: Run the real track.** Write a THROWAWAY console harness in the scratchpad (never in the repo) that decodes the user's `2017_LonelyHill2.mp3` at the repo root (**read-only** — never move, modify, delete, or commit it), runs `ChromaChordRecognizer`'s chain, and prints the resulting chord spans with timings. Report the first ~20 spans and your honest read on whether they look musically plausible (stable chords of sensible length in a consistent key, versus flickering nonsense). This is a sanity check, not a pass/fail gate.

- [ ] **Step 2: Record the truth in the spec.** Add a `### 13.6 Phase 5 rulings` section covering:
  - Chord recognition is chroma template matching, not BTC — user decision; §5's BTC path and §11's export risk are **not** attempted, and `NNLS-Chroma/Chordino` in §5 is the sanctioned precedent.
  - Vocabulary is 24 triads + "N"; sevenths excluded because 12-bin chroma cannot reliably distinguish them and a confidently-wrong seventh is worse for the modal engine than a correct triad.
  - Beat-synchronous chord boundaries are deferred.
  - The Phase-4 final review's request: §4's "free VRAM ≥ 6 GB" gate and "CUDA→DML" execution-provider order are superseded by §13.1 and Phase 4's CPU-EP-only ruling — say so plainly so the spec stops describing an executor that will never exist.
  - Whatever the real-track check showed, including if it was poor.

- [ ] **Step 3: Commit**

```powershell
git add docs/superpowers/specs/2026-08-16-pomode-design.md
git commit -m "docs: record Phase 5 rulings and the real-track chord check"
```

---

## Phase 5 Exit Criteria

- `dotnet test` green across all four projects (E2EUI re-checked alone if the full run starves it); zero build warnings; no `Version=` in any csproj; no secrets.
- `FakeChordRecognizer` is no longer selected by the planner; a completed job's plan contains **no** `Fake*` executor.
- Consequently the **"USING MOCK DATA" banner switches off** for a real analysis — the first time in the project's life. Verify it in the browser test and say so.
- Chord recognition works with no network and no downloaded model (delete `models/` and the stage still runs).
- The real-track check is recorded honestly in the spec, good or bad.
- Phase 6 starts from: the visualiser (canvas piano roll + chord timeline + HUD), the Ollama copilot, and the still-unbuilt cloud tier and WebGPU delegation.
