# PoMode Phase 3: Modal Engine & MIDI Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `PlaceholderModalAnalyzer` with the real deterministic `ModalAnalysisEngine` (spec §6), persist a `ModalResult` artifact, surface it in the UI, and export a 4-track Standard MIDI File.

**Architecture:** Pure-C# music theory in `Features/ModalAnalysis/` — mode masks derived from interval sets (never literals), Krumhansl-Schmuckler tonic detection over a duration-weighted pitch-class histogram, per-chord-window scoring with characteristic-degree weighting. The engine reads `notes.json` + `chords.json` (Phase 2 artifacts) through the existing `IModalAnalyzer` seam and writes `result.json`. `Features/MidiExport/` builds SMF Type 1 with DryWetMidi at a fixed 120 BPM (real tempo lands in Phase 4). The client adds a results card plus a download button.

**Tech Stack:** .NET 10, `Melanchall.DryWetMidi`, Radzen Blazor, xUnit, Playwright.

**Spec:** `docs/superpowers/specs/2026-08-16-pomode-design.md` (§6 modal engine, §8 MIDI export, §13 corrections).

**Plan-level rulings (decided now):**
- **Tempo is NOT in this phase** (user decision). MIDI Track 0 gets a fixed 120 BPM 4/4 map; measure numbers derive from it. Phase 4 replaces this with the real estimator. Every place this assumption leaks (MIDI, measure numbers, UI) says "120 BPM (estimated)".
- Chord windows come from `chords.json` exactly as-is; no re-segmentation.
- The engine is a pure function over artifacts — it never touches audio. This keeps it fully unit-testable today.
- Note→pitch-class uses `midi % 12` where pitch class 0 = C, matching MIDI convention (C4 = 60).
- K-S correlation uses the standard Krumhansl-Kessler major/minor profiles; the mode returned by K-S is only used to pick the **tonic**, never the final mode (the per-window engine decides modes).
- `ModalResult` is versioned (`SchemaVersion = 1`) so Phase 4+ can migrate old job folders instead of crashing.

## Global Constraints

- All prior constraints hold: `net10.0`, Nullable + TreatWarningsAsErrors from `Directory.Build.props` only; CPM (versions ONLY in `Directory.Packages.props`, added via `dotnet add`); `PoMode.` prefixes; no secrets; endpoints via `MapGroup()` + `TypedResults`; zero inline CSS (scoped `.razor.css` + `--pm-*` variables); Radzen controls.
- **TDD with log-file evidence is mandatory** (three tasks were bounced in earlier phases for reconstructed output): tee each RED and GREEN run to `<workspace>/task-N-{red,green,full}.log` and quote those files in the report. Reviewers read the logs directly.
- Commit hygiene: stage only the paths listed in each task's commit step. Never stage `.claude/`, `*.mp3`, `*.wav`, or files under `.superpowers/`.
- Commits: conventional style (`feat:`/`test:`/`fix:`/`chore:`) ending with `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- Run all commands from repo root `c:\Users\punko\Downloads\PoMode`.

---

### Task 1: Repo Hygiene — Ignore Local Settings

**Files:**
- Modify: `.gitignore`

**Interfaces:**
- Consumes: nothing.
- Produces: `.claude/` stays out of version control for every later task.

- [ ] **Step 1: Append to `.gitignore`**

```gitignore
.claude/
```

- [ ] **Step 2: Verify**

Run: `git status --porcelain`
Expected: no `?? .claude/` line; output is empty.

- [ ] **Step 3: Commit**

```powershell
git add .gitignore
git commit -m "chore: ignore local .claude settings directory"
```

---

### Task 2: Modal Contracts

**Files:**
- Create: `src/PoMode.Shared/Analysis/ModalContracts.cs`
- Modify: `src/PoMode.Shared/Serialization/PoModeJsonContext.cs`
- Test: `tests/PoMode.Unit/Serialization/JsonContextTests.cs` (append)

**Interfaces:**
- Consumes: `NoteEvent`, `ChordSpan` (Phase 2).
- Produces (consumed verbatim by Tasks 3–7):
  - `enum ScaleMode { Ionian, Dorian, Phrygian, Lydian, Mixolydian, Aeolian, Locrian, MinorPentatonic, MajorPentatonic }`
  - `record ModalMatch(ScaleMode Mode, double Confidence, IReadOnlyList<int> MatchedIntervals, IReadOnlyList<int> OutsideIntervals)`
  - `record ModalWindow(int Index, double StartSec, double EndSec, string ChordSymbol, int MeasureNumber, int VocalMask, IReadOnlyList<int> SungIntervals, bool InsufficientEvidence, IReadOnlyList<ModalMatch> Matches)`
  - `record ModalResult(int SchemaVersion, int TonicPitchClass, string TonicName, double TonicConfidence, ScaleMode? PrimaryMode, double PrimaryConfidence, double TempoBpm, bool TempoEstimated, IReadOnlyList<ModalWindow> Windows)`
  - `PoModeJsonContext.Default.ModalResult`

- [ ] **Step 1: Write the failing test** — append to `tests/PoMode.Unit/Serialization/JsonContextTests.cs`:

```csharp
[Fact]
public void ModalResult_round_trips_via_source_gen_context()
{
    var result = new ModalResult(
        SchemaVersion: 1,
        TonicPitchClass: 2,
        TonicName: "D",
        TonicConfidence: 0.82,
        PrimaryMode: ScaleMode.Dorian,
        PrimaryConfidence: 0.9,
        TempoBpm: 120.0,
        TempoEstimated: true,
        Windows:
        [
            new ModalWindow(
                Index: 0,
                StartSec: 0,
                EndSec: 2,
                ChordSymbol: "Dm7",
                MeasureNumber: 1,
                VocalMask: 0b011010101101,
                SungIntervals: [0, 2, 3, 5, 7, 9, 10],
                InsufficientEvidence: false,
                Matches: [new ModalMatch(ScaleMode.Dorian, 1.0, [0, 2, 3], [])])
        ]);

    var json = JsonSerializer.Serialize(result, PoModeJsonContext.Default.ModalResult);
    var back = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.ModalResult);

    Assert.NotNull(back);
    Assert.Equal(ScaleMode.Dorian, back.PrimaryMode);
    Assert.Equal("D", back.TonicName);
    Assert.Equal(1, back.Windows[0].MeasureNumber);
    Assert.Equal(ScaleMode.Dorian, back.Windows[0].Matches[0].Mode);
    Assert.True(back.TempoEstimated);
}
```

- [ ] **Step 2: RED** — `dotnet test tests/PoMode.Unit --filter JsonContextTests` (tee to `task-2-red.log`). Expected: compile errors, `ModalResult` missing.

- [ ] **Step 3: Implement** — `src/PoMode.Shared/Analysis/ModalContracts.cs`:

```csharp
namespace PoMode.Shared.Analysis;

public enum ScaleMode
{
    Ionian,
    Dorian,
    Phrygian,
    Lydian,
    Mixolydian,
    Aeolian,
    Locrian,
    MinorPentatonic,
    MajorPentatonic,
}

public sealed record ModalMatch(
    ScaleMode Mode,
    double Confidence,
    IReadOnlyList<int> MatchedIntervals,
    IReadOnlyList<int> OutsideIntervals);

public sealed record ModalWindow(
    int Index,
    double StartSec,
    double EndSec,
    string ChordSymbol,
    int MeasureNumber,
    int VocalMask,
    IReadOnlyList<int> SungIntervals,
    bool InsufficientEvidence,
    IReadOnlyList<ModalMatch> Matches);

/// <summary>Whole-song modal analysis. SchemaVersion lets later phases migrate old job folders.</summary>
public sealed record ModalResult(
    int SchemaVersion,
    int TonicPitchClass,
    string TonicName,
    double TonicConfidence,
    ScaleMode? PrimaryMode,
    double PrimaryConfidence,
    double TempoBpm,
    bool TempoEstimated,
    IReadOnlyList<ModalWindow> Windows);
```

Add `[JsonSerializable(typeof(ModalResult))]` to `PoModeJsonContext`.

- [ ] **Step 4: GREEN** — `dotnet test` (tee to `task-2-green.log`). Expected: 62 passed, zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.Shared tests/PoMode.Unit/Serialization/JsonContextTests.cs
git commit -m "feat: modal analysis contracts with versioned result schema"
```

---

### Task 3: Mode Masks & Music Theory Primitives

**Files:**
- Create: `src/PoMode.API/Features/ModalAnalysis/ModeDefinitions.cs`, `src/PoMode.API/Features/ModalAnalysis/PitchNames.cs`
- Test: `tests/PoMode.Unit/ModalAnalysis/ModeDefinitionsTests.cs`

**Interfaces:**
- Consumes: `ScaleMode` (Task 2).
- Produces:
  - `ModeDefinitions.Intervals(ScaleMode)` → `IReadOnlyList<int>`
  - `ModeDefinitions.Mask(ScaleMode)` → `int` (12-bit, derived by folding `1 << i`; NO literals)
  - `ModeDefinitions.CharacteristicIntervals(ScaleMode)` → `IReadOnlyList<int>`
  - `ModeDefinitions.All` → `IReadOnlyList<ScaleMode>`
  - `PitchNames.Name(int pitchClass)` → `"C"`, `"C#"`, … (sharps)
  - `PitchNames.IntervalLabel(int semitones)` → `"1"`, `"b2"`, `"2"`, `"b3"`, `"3"`, `"4"`, `"#4"`, `"5"`, `"b6"`, `"6"`, `"b7"`, `"7"`

**Canonical masks (spec §6) — the test asserts these exact values:** Ionian 0xAB5, Dorian 0x6AD, Phrygian 0x5AB, Lydian 0xAD5, Mixolydian 0x6B5, Aeolian 0x5AD, Locrian 0x56B, MinorPentatonic 0x4A9, MajorPentatonic 0x295.

- [ ] **Step 1: Write the failing test** — `tests/PoMode.Unit/ModalAnalysis/ModeDefinitionsTests.cs`:

```csharp
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ModeDefinitionsTests
{
    [Theory]
    [InlineData(ScaleMode.Ionian, 0xAB5)]
    [InlineData(ScaleMode.Dorian, 0x6AD)]
    [InlineData(ScaleMode.Phrygian, 0x5AB)]
    [InlineData(ScaleMode.Lydian, 0xAD5)]
    [InlineData(ScaleMode.Mixolydian, 0x6B5)]
    [InlineData(ScaleMode.Aeolian, 0x5AD)]
    [InlineData(ScaleMode.Locrian, 0x56B)]
    [InlineData(ScaleMode.MinorPentatonic, 0x4A9)]
    [InlineData(ScaleMode.MajorPentatonic, 0x295)]
    public void Masks_match_the_canonical_spec_table(ScaleMode mode, int expected)
        => Assert.Equal(expected, ModeDefinitions.Mask(mode));

    [Fact]
    public void Every_mode_has_a_definition_and_root_is_always_present()
    {
        Assert.Equal(9, ModeDefinitions.All.Count);
        foreach (var mode in ModeDefinitions.All)
        {
            Assert.Contains(0, ModeDefinitions.Intervals(mode));
            Assert.All(ModeDefinitions.Intervals(mode), i => Assert.InRange(i, 0, 11));
            Assert.Equal(ModeDefinitions.Intervals(mode).Count, ModeDefinitions.Intervals(mode).Distinct().Count());
        }
    }

    [Fact]
    public void Seven_note_modes_have_seven_notes_and_pentatonics_have_five()
    {
        Assert.Equal(7, ModeDefinitions.Intervals(ScaleMode.Ionian).Count);
        Assert.Equal(7, ModeDefinitions.Intervals(ScaleMode.Locrian).Count);
        Assert.Equal(5, ModeDefinitions.Intervals(ScaleMode.MinorPentatonic).Count);
        Assert.Equal(5, ModeDefinitions.Intervals(ScaleMode.MajorPentatonic).Count);
    }

    [Theory]
    [InlineData(ScaleMode.Dorian, 9)]     // natural 6
    [InlineData(ScaleMode.Lydian, 6)]     // sharp 4
    [InlineData(ScaleMode.Phrygian, 1)]   // flat 2
    [InlineData(ScaleMode.Mixolydian, 10)] // flat 7
    [InlineData(ScaleMode.Locrian, 6)]    // flat 5
    public void Characteristic_intervals_are_in_the_mode(ScaleMode mode, int interval)
    {
        Assert.Contains(interval, ModeDefinitions.CharacteristicIntervals(mode));
        Assert.Contains(interval, ModeDefinitions.Intervals(mode));
    }

    [Theory]
    [InlineData(0, "C")]
    [InlineData(1, "C#")]
    [InlineData(9, "A")]
    [InlineData(11, "B")]
    public void Pitch_names_use_sharps(int pitchClass, string expected)
        => Assert.Equal(expected, PitchNames.Name(pitchClass));

    [Theory]
    [InlineData(0, "1")]
    [InlineData(1, "b2")]
    [InlineData(3, "b3")]
    [InlineData(6, "#4")]
    [InlineData(10, "b7")]
    public void Interval_labels_use_flat_and_sharp_degrees(int semitones, string expected)
        => Assert.Equal(expected, PitchNames.IntervalLabel(semitones));
}
```

- [ ] **Step 2: RED** — `dotnet test tests/PoMode.Unit --filter ModeDefinitionsTests` (tee `task-3-red.log`).

- [ ] **Step 3: Implement**

`src/PoMode.API/Features/ModalAnalysis/ModeDefinitions.cs`:
```csharp
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Mode interval sets and their derived 12-bit masks. Masks are computed, never written as literals.</summary>
public static class ModeDefinitions
{
    private static readonly Dictionary<ScaleMode, int[]> IntervalSets = new()
    {
        [ScaleMode.Ionian] = [0, 2, 4, 5, 7, 9, 11],
        [ScaleMode.Dorian] = [0, 2, 3, 5, 7, 9, 10],
        [ScaleMode.Phrygian] = [0, 1, 3, 5, 7, 8, 10],
        [ScaleMode.Lydian] = [0, 2, 4, 6, 7, 9, 11],
        [ScaleMode.Mixolydian] = [0, 2, 4, 5, 7, 9, 10],
        [ScaleMode.Aeolian] = [0, 2, 3, 5, 7, 8, 10],
        [ScaleMode.Locrian] = [0, 1, 3, 5, 6, 8, 10],
        [ScaleMode.MinorPentatonic] = [0, 3, 5, 7, 10],
        [ScaleMode.MajorPentatonic] = [0, 2, 4, 7, 9],
    };

    /// <summary>Degrees that distinguish a mode from its nearest neighbours; weighted extra when sung.</summary>
    private static readonly Dictionary<ScaleMode, int[]> Characteristic = new()
    {
        [ScaleMode.Ionian] = [11],           // major 7
        [ScaleMode.Dorian] = [9, 3],         // natural 6 over a minor 3
        [ScaleMode.Phrygian] = [1],          // flat 2
        [ScaleMode.Lydian] = [6],            // sharp 4
        [ScaleMode.Mixolydian] = [10, 4],    // flat 7 over a major 3
        [ScaleMode.Aeolian] = [8, 3],        // flat 6 over a minor 3
        [ScaleMode.Locrian] = [6, 1],        // flat 5 and flat 2
        [ScaleMode.MinorPentatonic] = [3, 10],
        [ScaleMode.MajorPentatonic] = [4, 9],
    };

    public static IReadOnlyList<ScaleMode> All { get; } = [.. IntervalSets.Keys];

    public static IReadOnlyList<int> Intervals(ScaleMode mode) => IntervalSets[mode];

    public static IReadOnlyList<int> CharacteristicIntervals(ScaleMode mode) => Characteristic[mode];

    public static int Mask(ScaleMode mode)
    {
        var mask = 0;
        foreach (var interval in IntervalSets[mode])
        {
            mask |= 1 << interval;
        }
        return mask;
    }
}
```

`src/PoMode.API/Features/ModalAnalysis/PitchNames.cs`:
```csharp
namespace PoMode.API.Features.ModalAnalysis;

public static class PitchNames
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly string[] Degrees = ["1", "b2", "2", "b3", "3", "4", "#4", "5", "b6", "6", "b7", "7"];

    public static string Name(int pitchClass) => Names[((pitchClass % 12) + 12) % 12];

    public static string IntervalLabel(int semitones) => Degrees[((semitones % 12) + 12) % 12];
}
```

- [ ] **Step 4: GREEN** — `dotnet test` (tee `task-3-green.log`). Expected: 82 passed (62 + 20 new theory/fact cases), zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.API/Features/ModalAnalysis tests/PoMode.Unit/ModalAnalysis
git commit -m "feat: mode interval definitions with derived bitmasks and pitch naming"
```

---

### Task 4: Tonic Detection (Krumhansl-Schmuckler)

**Files:**
- Create: `src/PoMode.API/Features/ModalAnalysis/TonicDetector.cs`
- Test: `tests/PoMode.Unit/ModalAnalysis/TonicDetectorTests.cs`

**Interfaces:**
- Consumes: `NoteEvent`, `ChordSpan`.
- Produces: `record TonicEstimate(int PitchClass, double Confidence)`; `static TonicEstimate TonicDetector.Detect(IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords)`.

**Algorithm:** build a 12-slot histogram — each note adds `DurationSec` to `midi % 12`; each chord adds `(EndSec - StartSec) * 0.5` to its root pitch class. Correlate (Pearson) the histogram against both Krumhansl-Kessler profiles rotated to all 12 roots; take the best of the 24. `Confidence` = `(best - secondBest) / best`, clamped to `[0,1]` (0 when `best <= 0`). Empty input → `(0, 0.0)`.

Krumhansl-Kessler profiles (use verbatim):
```
major = 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88
minor = 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17
```

Chord-root parsing: the root is `ChordSpan.Root` (e.g. `"A"`, `"Bb"`, `"F#"`). Map letter → base pitch class (C0 D2 E4 F5 G7 A9 B11), then `#`/`b` adjust ±1, wrapped.

- [ ] **Step 1: Write the failing test** — `tests/PoMode.Unit/ModalAnalysis/TonicDetectorTests.cs`:

```csharp
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class TonicDetectorTests
{
    private static NoteEvent Note(int midi, double dur = 1.0) => new(midi, 0, dur, 96);

    [Fact]
    public void C_major_scale_and_chords_detect_C()
    {
        List<NoteEvent> notes = [Note(60, 2), Note(62), Note(64, 1.5), Note(65), Note(67, 2), Note(69), Note(71)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2), new("F", "F", "maj", 2, 4), new("G", "G", "maj", 4, 6), new("C", "C", "maj", 6, 8)];

        var tonic = TonicDetector.Detect(notes, chords);

        Assert.Equal(0, tonic.PitchClass);
        Assert.InRange(tonic.Confidence, 0.0, 1.0);
    }

    [Fact]
    public void A_minor_material_detects_A()
    {
        List<NoteEvent> notes = [Note(69, 3), Note(71), Note(72, 1.5), Note(74), Note(76, 2), Note(77), Note(79)];
        List<ChordSpan> chords = [new("Am", "A", "min", 0, 2), new("Dm", "D", "min", 2, 4), new("Em", "E", "min", 4, 6), new("Am", "A", "min", 6, 8)];

        Assert.Equal(9, TonicDetector.Detect(notes, chords).PitchClass);
    }

    [Fact]
    public void Flat_and_sharp_chord_roots_are_parsed()
    {
        List<ChordSpan> chords = [new("Bb", "Bb", "maj", 0, 4), new("F#m", "F#", "min", 4, 6)];
        var tonic = TonicDetector.Detect([], chords);

        // Bb dominates the histogram; the detector must not throw and must return a valid class.
        Assert.InRange(tonic.PitchClass, 0, 11);
    }

    [Fact]
    public void Empty_input_is_zero_confidence()
    {
        var tonic = TonicDetector.Detect([], []);

        Assert.Equal(0.0, tonic.Confidence);
    }

    [Fact]
    public void Transposing_everything_transposes_the_tonic()
    {
        List<NoteEvent> cMajor = [Note(60, 2), Note(64), Note(67, 2), Note(71)];
        List<ChordSpan> cChords = [new("C", "C", "maj", 0, 4)];
        List<NoteEvent> dMajor = [.. cMajor.Select(n => n with { MidiPitch = n.MidiPitch + 2 })];
        List<ChordSpan> dChords = [new("D", "D", "maj", 0, 4)];

        var c = TonicDetector.Detect(cMajor, cChords).PitchClass;
        var d = TonicDetector.Detect(dMajor, dChords).PitchClass;

        Assert.Equal((c + 2) % 12, d);
    }
}
```

- [ ] **Step 2: RED** — `dotnet test tests/PoMode.Unit --filter TonicDetectorTests` (tee `task-4-red.log`).

- [ ] **Step 3: Implement** — `src/PoMode.API/Features/ModalAnalysis/TonicDetector.cs`:

```csharp
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

public sealed record TonicEstimate(int PitchClass, double Confidence);

/// <summary>Krumhansl-Schmuckler key finding. Only the ROOT is used downstream; the major/minor
/// verdict is discarded because the per-window engine decides modes.</summary>
public static class TonicDetector
{
    private static readonly double[] MajorProfile =
        [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];

    private static readonly double[] MinorProfile =
        [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    public static TonicEstimate Detect(IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords)
    {
        var histogram = new double[12];
        foreach (var note in notes)
        {
            histogram[((note.MidiPitch % 12) + 12) % 12] += Math.Max(note.DurationSec, 0);
        }
        foreach (var chord in chords)
        {
            if (TryParseRoot(chord.Root, out var pitchClass))
            {
                histogram[pitchClass] += Math.Max(chord.EndSec - chord.StartSec, 0) * 0.5;
            }
        }

        if (histogram.Sum() <= 0)
        {
            return new TonicEstimate(0, 0.0);
        }

        var best = double.NegativeInfinity;
        var second = double.NegativeInfinity;
        var bestPitchClass = 0;
        for (var root = 0; root < 12; root++)
        {
            foreach (var profile in new[] { MajorProfile, MinorProfile })
            {
                var score = Correlate(histogram, profile, root);
                if (score > best)
                {
                    second = best;
                    best = score;
                    bestPitchClass = root;
                }
                else if (score > second)
                {
                    second = score;
                }
            }
        }

        var confidence = best <= 0 ? 0.0 : Math.Clamp((best - second) / best, 0.0, 1.0);
        return new TonicEstimate(bestPitchClass, confidence);
    }

    public static bool TryParseRoot(string root, out int pitchClass)
    {
        pitchClass = 0;
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var baseClass = char.ToUpperInvariant(root[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => -1,
        };
        if (baseClass < 0)
        {
            return false;
        }

        foreach (var accidental in root.Skip(1))
        {
            baseClass += accidental switch { '#' => 1, 'b' => -1, _ => 0 };
        }

        pitchClass = ((baseClass % 12) + 12) % 12;
        return true;
    }

    private static double Correlate(double[] histogram, double[] profile, int rotation)
    {
        var meanHistogram = histogram.Average();
        var meanProfile = profile.Average();
        double covariance = 0, histogramVariance = 0, profileVariance = 0;
        for (var i = 0; i < 12; i++)
        {
            var h = histogram[i] - meanHistogram;
            var p = profile[((i - rotation) % 12 + 12) % 12] - meanProfile;
            covariance += h * p;
            histogramVariance += h * h;
            profileVariance += p * p;
        }
        var denominator = Math.Sqrt(histogramVariance * profileVariance);
        return denominator <= 0 ? 0 : covariance / denominator;
    }
}
```

- [ ] **Step 4: GREEN** — `dotnet test` (tee `task-4-green.log`). Expected: 87 passed, zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.API/Features/ModalAnalysis tests/PoMode.Unit/ModalAnalysis
git commit -m "feat: Krumhansl-Schmuckler tonic detection over notes and chord roots"
```

---

### Task 5: ModalAnalysisEngine (Window Scoring)

**Files:**
- Create: `src/PoMode.API/Features/ModalAnalysis/ModalAnalysisEngine.cs`
- Test: `tests/PoMode.Unit/ModalAnalysis/ModalAnalysisEngineTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–4.
- Produces: `static ModalResult ModalAnalysisEngine.Analyze(IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords, double tempoBpm = 120.0)`.

**Algorithm (spec §6):**
1. Tonic via `TonicDetector`.
2. Per chord window: sung pitch classes = distinct `midi % 12` of notes whose `StartSec` is inside `[StartSec, EndSec)`. Intervals = `(pc - tonic + 12) % 12`, sorted. `VocalMask` = OR of `1 << interval`.
3. `< 3` distinct intervals → `InsufficientEvidence = true`, `Matches = []`.
4. Otherwise score every mode: `coverage = popcount(VocalMask & modeMask) / popcount(VocalMask)`; `bonus = 0.15 * (characteristic intervals present / characteristic count)`; `confidence = Math.Clamp(coverage + bonus, 0, 1)` — but a mode with any outside note can never reach 1.0, so when `coverage < 1` cap at `0.99`. Order by confidence desc, then by fewer intervals in the mode (prefers the tighter fit), then by enum order for stability. Keep the top 4.
5. `MeasureNumber` = `(int)(StartSec / secondsPerMeasure) + 1` where `secondsPerMeasure = 4 * 60 / tempoBpm` (4/4).
6. Primary mode = highest total confidence weighted by window duration across all non-insufficient windows; null when every window is insufficient. `PrimaryConfidence` = that weighted average.

- [ ] **Step 1: Write the failing test** — `tests/PoMode.Unit/ModalAnalysis/ModalAnalysisEngineTests.cs`:

```csharp
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ModalAnalysisEngineTests
{
    private static NoteEvent At(int midi, double start) => new(midi, start, 0.4, 96);

    [Fact]
    public void D_dorian_material_scores_dorian_top()
    {
        // Tonic D (62). Sing D E F G A B C — the natural 6 (B) is the Dorian marker.
        List<NoteEvent> notes =
        [
            At(62, 0.0), At(64, 0.2), At(65, 0.4), At(67, 0.6),
            At(69, 0.8), At(71, 1.0), At(72, 1.2), At(62, 1.4),
        ];
        List<ChordSpan> chords = [new("Dm7", "D", "min7", 0, 2)];

        var result = ModalAnalysisEngine.Analyze(notes, chords);

        Assert.Equal(2, result.TonicPitchClass);
        Assert.Equal("D", result.TonicName);
        Assert.Equal(ScaleMode.Dorian, result.Windows[0].Matches[0].Mode);
        Assert.Equal(ScaleMode.Dorian, result.PrimaryMode);
        Assert.False(result.Windows[0].InsufficientEvidence);
    }

    [Fact]
    public void Window_with_two_pitch_classes_reports_insufficient_evidence()
    {
        List<NoteEvent> notes = [At(60, 0.0), At(67, 0.5)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var result = ModalAnalysisEngine.Analyze(notes, chords);

        Assert.True(result.Windows[0].InsufficientEvidence);
        Assert.Empty(result.Windows[0].Matches);
        Assert.Null(result.PrimaryMode);
    }

    [Fact]
    public void Notes_are_assigned_to_the_window_containing_their_start()
    {
        List<NoteEvent> notes = [At(60, 0.5), At(62, 0.9), At(64, 1.1), At(65, 2.5), At(67, 2.9), At(69, 3.1)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2), new("F", "F", "maj", 2, 4)];

        var result = ModalAnalysisEngine.Analyze(notes, chords);

        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(3, result.Windows[0].SungIntervals.Count);
        Assert.Equal(3, result.Windows[1].SungIntervals.Count);
    }

    [Fact]
    public void Vocal_mask_matches_the_sung_intervals()
    {
        List<NoteEvent> notes = [At(60, 0.0), At(62, 0.3), At(64, 0.6), At(65, 0.9)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var window = ModalAnalysisEngine.Analyze(notes, chords).Windows[0];

        var expected = window.SungIntervals.Aggregate(0, (mask, interval) => mask | (1 << interval));
        Assert.Equal(expected, window.VocalMask);
    }

    [Fact]
    public void Measure_numbers_follow_the_tempo_at_four_four()
    {
        // 120 BPM ⇒ 2 s per measure.
        List<NoteEvent> notes = [At(60, 0.1), At(62, 0.3), At(64, 0.5), At(65, 4.1), At(67, 4.3), At(69, 4.5)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2), new("F", "F", "maj", 4, 6)];

        var result = ModalAnalysisEngine.Analyze(notes, chords, tempoBpm: 120.0);

        Assert.Equal(1, result.Windows[0].MeasureNumber);
        Assert.Equal(3, result.Windows[1].MeasureNumber);
        Assert.Equal(120.0, result.TempoBpm);
        Assert.True(result.TempoEstimated);
    }

    [Fact]
    public void Matches_are_ranked_and_capped_at_four()
    {
        List<NoteEvent> notes = [At(60, 0.0), At(62, 0.2), At(64, 0.4), At(65, 0.6), At(67, 0.8), At(69, 1.0), At(71, 1.2)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var matches = ModalAnalysisEngine.Analyze(notes, chords).Windows[0].Matches;

        Assert.InRange(matches.Count, 1, 4);
        Assert.Equal(ScaleMode.Ionian, matches[0].Mode);
        for (var i = 1; i < matches.Count; i++)
        {
            Assert.True(matches[i - 1].Confidence >= matches[i].Confidence);
        }
    }

    [Fact]
    public void Outside_notes_are_reported_and_cap_confidence_below_one()
    {
        // C Ionian plus a b2 (C#) that no Ionian scale contains.
        List<NoteEvent> notes = [At(60, 0.0), At(61, 0.2), At(64, 0.4), At(65, 0.6), At(67, 0.8)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var top = ModalAnalysisEngine.Analyze(notes, chords).Windows[0].Matches[0];

        Assert.True(top.Confidence < 1.0);
        Assert.NotEmpty(top.OutsideIntervals);
    }

    [Fact]
    public void Result_carries_schema_version_one()
        => Assert.Equal(1, ModalAnalysisEngine.Analyze([], []).SchemaVersion);
}
```

- [ ] **Step 2: RED** — `dotnet test tests/PoMode.Unit --filter ModalAnalysisEngineTests` (tee `task-5-red.log`).

- [ ] **Step 3: Implement** — `src/PoMode.API/Features/ModalAnalysis/ModalAnalysisEngine.cs`:

```csharp
using System.Numerics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Deterministic modal derivation (spec §6). Pure function over Phase-2 artifacts — no audio, no models.</summary>
public static class ModalAnalysisEngine
{
    private const int MinimumDistinctPitchClasses = 3;
    private const int MaxMatchesPerWindow = 4;
    private const double CharacteristicBonus = 0.15;

    public static ModalResult Analyze(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<ChordSpan> chords,
        double tempoBpm = 120.0)
    {
        var tonic = TonicDetector.Detect(notes, chords);
        var secondsPerMeasure = 4.0 * 60.0 / (tempoBpm <= 0 ? 120.0 : tempoBpm);

        var windows = new List<ModalWindow>(chords.Count);
        for (var index = 0; index < chords.Count; index++)
        {
            var chord = chords[index];
            var intervals = notes
                .Where(note => note.StartSec >= chord.StartSec && note.StartSec < chord.EndSec)
                .Select(note => (((note.MidiPitch % 12) + 12) % 12 - tonic.PitchClass + 12) % 12)
                .Distinct()
                .Order()
                .ToArray();

            var vocalMask = intervals.Aggregate(0, (mask, interval) => mask | (1 << interval));
            var insufficient = intervals.Length < MinimumDistinctPitchClasses;
            var matches = insufficient ? [] : ScoreModes(vocalMask, intervals);

            windows.Add(new ModalWindow(
                Index: index,
                StartSec: chord.StartSec,
                EndSec: chord.EndSec,
                ChordSymbol: chord.Symbol,
                MeasureNumber: (int)(chord.StartSec / secondsPerMeasure) + 1,
                VocalMask: vocalMask,
                SungIntervals: intervals,
                InsufficientEvidence: insufficient,
                Matches: matches));
        }

        var (primaryMode, primaryConfidence) = PickPrimary(windows);

        return new ModalResult(
            SchemaVersion: 1,
            TonicPitchClass: tonic.PitchClass,
            TonicName: PitchNames.Name(tonic.PitchClass),
            TonicConfidence: tonic.Confidence,
            PrimaryMode: primaryMode,
            PrimaryConfidence: primaryConfidence,
            TempoBpm: tempoBpm,
            TempoEstimated: true,
            Windows: windows);
    }

    private static IReadOnlyList<ModalMatch> ScoreModes(int vocalMask, int[] intervals)
    {
        var sungCount = BitOperations.PopCount((uint)vocalMask);
        var scored = new List<(ModalMatch Match, int ModeSize)>();

        foreach (var mode in ModeDefinitions.All)
        {
            var modeMask = ModeDefinitions.Mask(mode);
            var matched = intervals.Where(i => (modeMask & (1 << i)) != 0).ToArray();
            var outside = intervals.Where(i => (modeMask & (1 << i)) == 0).ToArray();
            var coverage = sungCount == 0 ? 0 : (double)matched.Length / sungCount;

            var characteristic = ModeDefinitions.CharacteristicIntervals(mode);
            var present = characteristic.Count(i => (vocalMask & (1 << i)) != 0);
            var bonus = characteristic.Count == 0 ? 0 : CharacteristicBonus * present / characteristic.Count;

            var confidence = Math.Clamp(coverage + bonus, 0.0, coverage < 1.0 ? 0.99 : 1.0);
            scored.Add((new ModalMatch(mode, confidence, matched, outside), ModeDefinitions.Intervals(mode).Count));
        }

        return scored
            .OrderByDescending(entry => entry.Match.Confidence)
            .ThenBy(entry => entry.ModeSize)
            .ThenBy(entry => (int)entry.Match.Mode)
            .Take(MaxMatchesPerWindow)
            .Select(entry => entry.Match)
            .ToArray();
    }

    private static (ScaleMode? Mode, double Confidence) PickPrimary(List<ModalWindow> windows)
    {
        var usable = windows.Where(window => !window.InsufficientEvidence && window.Matches.Count > 0).ToArray();
        if (usable.Length == 0)
        {
            return (null, 0.0);
        }

        var totals = new Dictionary<ScaleMode, double>();
        var weights = new Dictionary<ScaleMode, double>();
        foreach (var window in usable)
        {
            var top = window.Matches[0];
            var weight = Math.Max(window.EndSec - window.StartSec, 0.0001);
            totals[top.Mode] = totals.GetValueOrDefault(top.Mode) + top.Confidence * weight;
            weights[top.Mode] = weights.GetValueOrDefault(top.Mode) + weight;
        }

        var winner = totals.OrderByDescending(pair => pair.Value).ThenBy(pair => (int)pair.Key).First();
        return (winner.Key, totals[winner.Key] / weights[winner.Key]);
    }
}
```

- [ ] **Step 4: GREEN** — `dotnet test` (tee `task-5-green.log`). Expected: 95 passed, zero warnings.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.API/Features/ModalAnalysis tests/PoMode.Unit/ModalAnalysis
git commit -m "feat: deterministic modal analysis engine with window scoring"
```

---

### Task 6: Wire the Engine into the Pipeline

**Files:**
- Create: `src/PoMode.API/Features/ModalAnalysis/ArtifactModalAnalyzer.cs`
- Delete: `src/PoMode.API/Features/ModalAnalysis/PlaceholderModalAnalyzer.cs`
- Modify: `src/PoMode.API/Program.cs` (DI registration swap), `src/PoMode.API/Features/Analysis/AnalysisEndpoints.cs` (add `/result` artifact route)
- Test: `tests/PoMode.Integration/ArtifactModalAnalyzerTests.cs`, `tests/PoMode.E2EAPI/AnalysisApiTests.cs` (append)

**Interfaces:**
- Consumes: `IModalAnalyzer`, `StageContext` (Phase 2); `ModalAnalysisEngine` (Task 5).
- Produces: `ArtifactModalAnalyzer` reads `notes.json` + `chords.json` from `context.JobDir`, runs the engine, writes `result.json`; `GET /api/analysis/{jobId}/result` → 200 `ModalResult` | 404.

- [ ] **Step 1: Write the failing tests**

`tests/PoMode.Integration/ArtifactModalAnalyzerTests.cs`:
```csharp
using System.Text.Json;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Integration;

public sealed class ArtifactModalAnalyzerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), $"pomode-modal-{Guid.NewGuid():N}");

    public ArtifactModalAnalyzerTests() => Directory.CreateDirectory(_jobDir);

    public void Dispose() => Directory.Delete(_jobDir, recursive: true);

    private StageContext Context() => new("job1", _jobDir, Path.Combine(_jobDir, "input.wav"));

    private async Task WriteArtifactsAsync(IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords)
    {
        await File.WriteAllTextAsync(Path.Combine(_jobDir, "notes.json"), JsonSerializer.Serialize(notes, Json));
        await File.WriteAllTextAsync(Path.Combine(_jobDir, "chords.json"), JsonSerializer.Serialize(chords, Json));
    }

    [Fact]
    public async Task Writes_result_json_from_the_note_and_chord_artifacts()
    {
        await WriteArtifactsAsync(
            [new(62, 0.0, 0.4, 96), new(65, 0.5, 0.4, 96), new(69, 1.0, 0.4, 96), new(71, 1.5, 0.4, 96)],
            [new("Dm7", "D", "min7", 0, 2)]);

        await new ArtifactModalAnalyzer().AnalyzeAsync(Context(), CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(_jobDir, "result.json"));
        var result = JsonSerializer.Deserialize<ModalResult>(text, Json);

        Assert.NotNull(result);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Single(result.Windows);
        Assert.Equal("Dm7", result.Windows[0].ChordSymbol);
    }

    [Fact]
    public async Task Missing_artifacts_produce_an_empty_result_rather_than_throwing()
    {
        await new ArtifactModalAnalyzer().AnalyzeAsync(Context(), CancellationToken.None);

        var result = JsonSerializer.Deserialize<ModalResult>(
            await File.ReadAllTextAsync(Path.Combine(_jobDir, "result.json")), Json);

        Assert.NotNull(result);
        Assert.Empty(result.Windows);
        Assert.Null(result.PrimaryMode);
    }
}
```

Append to `tests/PoMode.E2EAPI/AnalysisApiTests.cs`:
```csharp
[Fact]
public async Task Completed_job_exposes_a_modal_result()
{
    await using var factory = Factory();
    using var client = factory.CreateClient();

    using var form = WavForm();
    var created = await (await client.PostAsync("/api/analysis", form)).Content.ReadFromJsonAsync<JobStatusDto>();
    await WaitForTerminalAsync(client, created!.JobId, new TaskCompletionSource<JobStatusDto>().Task);

    var result = await client.GetFromJsonAsync<ModalResult>($"/api/analysis/{created.JobId}/result");

    Assert.NotNull(result);
    Assert.Equal(1, result.SchemaVersion);
    Assert.Equal(4, result.Windows.Count); // FakeChordRecognizer emits four chords
    Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope/result")).StatusCode);
}
```

- [ ] **Step 2: RED** — run both filtered suites (tee `task-6-red.log`).

- [ ] **Step 3: Implement** — `src/PoMode.API/Features/ModalAnalysis/ArtifactModalAnalyzer.cs`:

```csharp
using System.Text.Json;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Stage 4: reads notes.json + chords.json, runs the deterministic engine, writes result.json.</summary>
public sealed class ArtifactModalAnalyzer : IModalAnalyzer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task AnalyzeAsync(StageContext context, CancellationToken ct)
    {
        var notes = await ReadAsync<NoteEvent>(context.JobDir, "notes.json", ct);
        var chords = await ReadAsync<ChordSpan>(context.JobDir, "chords.json", ct);

        var result = ModalAnalysisEngine.Analyze(notes, chords);

        await File.WriteAllTextAsync(
            Path.Combine(context.JobDir, "result.json"),
            JsonSerializer.Serialize(result, Json),
            ct);
    }

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(string jobDir, string fileName, CancellationToken ct)
    {
        var path = Path.Combine(jobDir, fileName);
        if (!File.Exists(path))
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<T>>(await File.ReadAllTextAsync(path, ct), Json) ?? [];
    }
}
```

Delete `PlaceholderModalAnalyzer.cs`. In `Program.cs` change the registration to `builder.Services.AddSingleton<IModalAnalyzer, ArtifactModalAnalyzer>();`.

In `AnalysisEndpoints.cs`, add `MapArtifact(group, "result", "result.json");` beside the existing notes/chords lines.

- [ ] **Step 4: GREEN** — `dotnet test` (tee `task-6-green.log`). Expected: 98 passed, zero warnings. NOTE: Phase-2's `FakeExecutorTests.PlaceholderModalAnalyzer_writes_result_json` test referenced the deleted class — delete that one test method (its behavior is now covered by `ArtifactModalAnalyzerTests`) and say so in the report.

- [ ] **Step 5: Commit**

```powershell
git add src/PoMode.API tests/PoMode.Integration tests/PoMode.E2EAPI tests/PoMode.Unit
git commit -m "feat: run the real modal engine as pipeline stage 4 and serve result.json"
```

---

### Task 7: MIDI Export

**Files:**
- Create: `src/PoMode.API/Features/MidiExport/MidiFileBuilder.cs`, `src/PoMode.API/Features/MidiExport/MidiExportEndpoints.cs`
- Modify: `src/PoMode.API/Program.cs` (map the endpoint group)
- Test: `tests/PoMode.Unit/MidiExport/MidiFileBuilderTests.cs`, `tests/PoMode.E2EAPI/MidiExportTests.cs`

**Interfaces:**
- Consumes: `NoteEvent`, `ChordSpan`, `ModalResult`, `JobStore`.
- Produces:
  - `static byte[] MidiFileBuilder.Build(IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords, ModalResult result)`
  - `GET /api/analysis/{jobId}/midi` → 200 `audio/midi` file `pomode-{jobId}.mid` | 404 when the job or its artifacts are missing

**Structure (spec §8):** SMF Type 1, 480 ticks/quarter. Track 0 = tempo (`result.TempoBpm`) + 4/4. Track 1 = vocal notes, program 80. Track 2 = chord voicings, program 0 — root position triad + seventh derived from `ChordSpan.Quality` (`min7`→[0,3,7,10], `maj7`→[0,4,7,11], `7`/`dom7`→[0,4,7,10], `min`→[0,3,7], `dim`→[0,3,6], `aug`→[0,4,8], default/`maj`→[0,4,7]), voiced from MIDI octave 3 (root pitch class + 48). Track 3 = marker meta-events at each window start: `"Mode: {TonicName} {Mode} | Chord: {ChordSymbol}"`, or `"Mode: unclear | Chord: {ChordSymbol}"` when the window is insufficient.

- [ ] **Step 1: Add the package**

```powershell
dotnet add src/PoMode.API package Melanchall.DryWetMidi
```

- [ ] **Step 2: Write the failing tests**

`tests/PoMode.Unit/MidiExport/MidiFileBuilderTests.cs`:
```csharp
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using PoMode.API.Features.MidiExport;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.MidiExport;

public class MidiFileBuilderTests
{
    private static ModalResult Result(double bpm = 120.0) => new(
        SchemaVersion: 1,
        TonicPitchClass: 2,
        TonicName: "D",
        TonicConfidence: 0.8,
        PrimaryMode: ScaleMode.Dorian,
        PrimaryConfidence: 0.9,
        TempoBpm: bpm,
        TempoEstimated: true,
        Windows:
        [
            new ModalWindow(0, 0, 2, "Dm7", 1, 0, [0, 3, 7], false,
                [new ModalMatch(ScaleMode.Dorian, 0.95, [0, 3, 7], [])]),
            new ModalWindow(1, 2, 4, "G7", 2, 0, [], true, []),
        ]);

    private static MidiFile Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return MidiFile.Read(stream);
    }

    [Fact]
    public void Builds_a_type_1_file_with_four_tracks()
    {
        var bytes = MidiFileBuilder.Build(
            [new(62, 0.0, 0.5, 96)],
            [new("Dm7", "D", "min7", 0, 2), new("G7", "G", "7", 2, 4)],
            Result());

        var file = Parse(bytes);

        Assert.Equal(MidiFileFormat.MultiTrack, file.OriginalFormat);
        Assert.Equal(4, file.GetTrackChunks().Count());
    }

    [Fact]
    public void Track_zero_carries_the_tempo_and_time_signature()
    {
        var file = Parse(MidiFileBuilder.Build([], [], Result(bpm: 90.0)));
        var conductor = file.GetTrackChunks().First();

        var tempo = conductor.Events.OfType<SetTempoEvent>().Single();
        Assert.Equal(90.0, 60_000_000.0 / tempo.MicrosecondsPerQuarterNote, precision: 1);
        Assert.Single(conductor.Events.OfType<TimeSignatureEvent>());
    }

    [Fact]
    public void Vocal_and_chord_tracks_use_the_specified_gm_programs()
    {
        var file = Parse(MidiFileBuilder.Build(
            [new(62, 0.0, 0.5, 96)],
            [new("Dm7", "D", "min7", 0, 2)],
            Result()));
        var tracks = file.GetTrackChunks().ToArray();

        Assert.Equal(80, (int)tracks[1].Events.OfType<ProgramChangeEvent>().Single().ProgramNumber);
        Assert.Equal(0, (int)tracks[2].Events.OfType<ProgramChangeEvent>().Single().ProgramNumber);
    }

    [Fact]
    public void Vocal_notes_survive_the_round_trip_with_pitch_and_velocity()
    {
        var file = Parse(MidiFileBuilder.Build(
            [new(62, 0.0, 0.5, 96), new(65, 1.0, 0.25, 80)],
            [new("Dm7", "D", "min7", 0, 2)],
            Result()));

        var notes = file.GetTrackChunks().ElementAt(1).GetNotes().ToArray();

        Assert.Equal(2, notes.Length);
        Assert.Equal(62, (int)notes[0].NoteNumber);
        Assert.Equal(96, (int)notes[0].Velocity);
        Assert.Equal(65, (int)notes[1].NoteNumber);
    }

    [Fact]
    public void Chord_voicings_match_the_quality()
    {
        var file = Parse(MidiFileBuilder.Build(
            [],
            [new("Dm7", "D", "min7", 0, 2), new("G7", "G", "7", 2, 4)],
            Result()));

        var chordNotes = file.GetTrackChunks().ElementAt(2).GetNotes().ToArray();

        // Dm7 = D F A C (4 notes), G7 = G B D F (4 notes)
        Assert.Equal(8, chordNotes.Length);
        Assert.Equal([50, 53, 57, 60], chordNotes.Take(4).Select(n => (int)n.NoteNumber).Order().ToArray());
    }

    [Fact]
    public void Marker_track_labels_each_window_including_unclear_ones()
    {
        var file = Parse(MidiFileBuilder.Build([], [new("Dm7", "D", "min7", 0, 2), new("G7", "G", "7", 2, 4)], Result()));

        var markers = file.GetTrackChunks().ElementAt(3).Events.OfType<MarkerEvent>().Select(m => m.Text).ToArray();

        Assert.Equal(2, markers.Length);
        Assert.Contains("Mode: D Dorian | Chord: Dm7", markers);
        Assert.Contains("Mode: unclear | Chord: G7", markers);
    }
}
```

`tests/PoMode.E2EAPI/MidiExportTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class MidiExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-midi-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b.UseSetting("Jobs:RootPath", _root));

    [Fact]
    public async Task Completed_job_exports_a_playable_midi_file()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var content = new ByteArrayContent(TestAudio.MakeWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent { { content, "file", "test.wav" } };
        var created = await (await client.PostAsync("/api/analysis", form)).Content.ReadFromJsonAsync<JobStatusDto>();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{created!.JobId}");
            if (status!.Stage is JobStage.Complete or JobStage.Failed) break;
            await Task.Delay(200);
        }

        var response = await client.GetAsync($"/api/analysis/{created!.JobId}/midi");

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("MThd"u8.ToArray(), bytes.Take(4).ToArray());
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public async Task Unknown_job_midi_is_404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope/midi")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/analysis/00000000000000000000000000000000/midi")).StatusCode);
    }
}
```

- [ ] **Step 3: RED** — run both filtered suites (tee `task-7-red.log`).

- [ ] **Step 4: Implement** — `src/PoMode.API/Features/MidiExport/MidiFileBuilder.cs`:

```csharp
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.MidiExport;

/// <summary>Builds a Standard MIDI File (Type 1) per spec §8. Tempo comes from the analysis result.</summary>
public static class MidiFileBuilder
{
    private const short TicksPerQuarter = 480;
    private const int VocalProgram = 80; // GM Lead 1 (square)
    private const int ChordProgram = 0;  // GM Acoustic Grand Piano
    private const int ChordOctaveOffset = 48;

    public static byte[] Build(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<ChordSpan> chords,
        ModalResult result)
    {
        var bpm = result.TempoBpm <= 0 ? 120.0 : result.TempoBpm;
        var ticksPerSecond = TicksPerQuarter * bpm / 60.0;

        var file = new MidiFile { TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarter) };
        file.Chunks.Add(BuildConductorTrack(bpm));
        file.Chunks.Add(BuildVocalTrack(notes, ticksPerSecond));
        file.Chunks.Add(BuildChordTrack(chords, ticksPerSecond));
        file.Chunks.Add(BuildMarkerTrack(result, ticksPerSecond));

        using var stream = new MemoryStream();
        file.Write(stream, format: MidiFileFormat.MultiTrack);
        return stream.ToArray();
    }

    private static TrackChunk BuildConductorTrack(double bpm)
    {
        var track = new TrackChunk();
        track.Events.Add(new SetTempoEvent((long)Math.Round(60_000_000.0 / bpm)));
        track.Events.Add(new TimeSignatureEvent(4, 4));
        return track;
    }

    private static TrackChunk BuildVocalTrack(IReadOnlyList<NoteEvent> notes, double ticksPerSecond)
    {
        var events = new List<(long Tick, MidiEvent Event)>();
        foreach (var note in notes)
        {
            var start = (long)Math.Round(note.StartSec * ticksPerSecond);
            var end = (long)Math.Round((note.StartSec + Math.Max(note.DurationSec, 0.01)) * ticksPerSecond);
            var pitch = (SevenBitNumber)Math.Clamp(note.MidiPitch, 0, 127);
            var velocity = (SevenBitNumber)Math.Clamp(note.Velocity, 1, 127);
            events.Add((start, new NoteOnEvent(pitch, velocity)));
            events.Add((end, new NoteOffEvent(pitch, (SevenBitNumber)0)));
        }
        return Assemble(events, VocalProgram);
    }

    private static TrackChunk BuildChordTrack(IReadOnlyList<ChordSpan> chords, double ticksPerSecond)
    {
        var events = new List<(long Tick, MidiEvent Event)>();
        foreach (var chord in chords)
        {
            if (!TonicDetector.TryParseRoot(chord.Root, out var rootClass))
            {
                continue;
            }
            var start = (long)Math.Round(chord.StartSec * ticksPerSecond);
            var end = (long)Math.Round(Math.Max(chord.EndSec, chord.StartSec + 0.01) * ticksPerSecond);
            foreach (var interval in VoicingFor(chord.Quality))
            {
                var pitch = (SevenBitNumber)Math.Clamp(rootClass + ChordOctaveOffset + interval, 0, 127);
                events.Add((start, new NoteOnEvent(pitch, (SevenBitNumber)72)));
                events.Add((end, new NoteOffEvent(pitch, (SevenBitNumber)0)));
            }
        }
        return Assemble(events, ChordProgram);
    }

    private static TrackChunk BuildMarkerTrack(ModalResult result, double ticksPerSecond)
    {
        var events = new List<(long Tick, MidiEvent Event)>();
        foreach (var window in result.Windows)
        {
            var label = window is { InsufficientEvidence: false, Matches.Count: > 0 }
                ? $"Mode: {result.TonicName} {window.Matches[0].Mode} | Chord: {window.ChordSymbol}"
                : $"Mode: unclear | Chord: {window.ChordSymbol}";
            events.Add(((long)Math.Round(window.StartSec * ticksPerSecond), new MarkerEvent(label)));
        }
        return Assemble(events, program: null);
    }

    private static int[] VoicingFor(string quality) => quality.ToLowerInvariant() switch
    {
        "min7" or "m7" => [0, 3, 7, 10],
        "maj7" => [0, 4, 7, 11],
        "7" or "dom7" => [0, 4, 7, 10],
        "min" or "m" => [0, 3, 7],
        "dim" => [0, 3, 6],
        "aug" => [0, 4, 8],
        _ => [0, 4, 7],
    };

    /// <summary>Sorts absolute-tick events and converts them to DryWetMidi's delta-time model.</summary>
    private static TrackChunk Assemble(List<(long Tick, MidiEvent Event)> events, int? program)
    {
        var track = new TrackChunk();
        if (program is not null)
        {
            track.Events.Add(new ProgramChangeEvent((SevenBitNumber)program.Value));
        }

        long previousTick = 0;
        foreach (var entry in events.OrderBy(e => e.Tick))
        {
            entry.Event.DeltaTime = entry.Tick - previousTick;
            previousTick = entry.Tick;
            track.Events.Add(entry.Event);
        }
        return track;
    }
}
```

`src/PoMode.API/Features/MidiExport/MidiExportEndpoints.cs`:
```csharp
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.MidiExport;

public static class MidiExportEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMidiExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analysis/{jobId}/midi", async Task<Results<FileContentHttpResult, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            if (jobId.Length != 32 || !jobId.All(char.IsAsciiHexDigitLower))
            {
                return TypedResults.NotFound();
            }

            var jobDir = store.JobDir(jobId);
            var resultPath = Path.Combine(jobDir, "result.json");
            if (!File.Exists(resultPath))
            {
                return TypedResults.NotFound();
            }

            var result = JsonSerializer.Deserialize<ModalResult>(await File.ReadAllTextAsync(resultPath, ct), Json);
            if (result is null)
            {
                return TypedResults.NotFound();
            }

            var notes = await ReadAsync<NoteEvent>(jobDir, "notes.json", ct);
            var chords = await ReadAsync<ChordSpan>(jobDir, "chords.json", ct);

            return TypedResults.File(
                MidiFileBuilder.Build(notes, chords, result),
                contentType: "audio/midi",
                fileDownloadName: $"pomode-{jobId}.mid");
        });

        return app;
    }

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(string jobDir, string fileName, CancellationToken ct)
    {
        var path = Path.Combine(jobDir, fileName);
        if (!File.Exists(path))
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<T>>(await File.ReadAllTextAsync(path, ct), Json) ?? [];
    }
}
```

In `Program.cs`, add `app.MapMidiExport();` beside `app.MapAnalysis();` (and the `using PoMode.API.Features.MidiExport;`).

- [ ] **Step 5: GREEN** — `dotnet test` (tee `task-7-green.log`). Expected: 106 passed, zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/PoMode.API tests/PoMode.Unit/MidiExport tests/PoMode.E2EAPI/MidiExportTests.cs Directory.Packages.props
git commit -m "feat: multi-track MIDI export with tempo, vocal, chord, and marker tracks"
```

---

### Task 8: Client — Show Modes, Download MIDI

**Files:**
- Create: `src/PoMode.Client/Components/ModalResultView.razor`, `src/PoMode.Client/Components/ModalResultView.razor.css`
- Modify: `src/PoMode.Client/Services/AnalysisClient.cs`, `src/PoMode.Client/Pages/Home.razor`, `src/PoMode.Client/Layout/MainLayout.razor`
- Test: `tests/PoMode.E2EUI/ModalResultTests.cs`

**Interfaces:**
- Consumes: `/api/analysis/{id}/result`, `/api/analysis/{id}/midi`, `ModalResult` contracts.
- Produces: `AnalysisClient.GetResultAsync(string jobId)`; `ModalResultView` component; a working "Export MIDI" link in the header.

- [ ] **Step 1: Extend the client service** — add to `AnalysisClient`:

```csharp
public Task<ModalResult?> GetResultAsync(string jobId)
    => http.GetFromJsonAsync<ModalResult>($"api/analysis/{jobId}/result");
```

- [ ] **Step 2: Create the view** — `src/PoMode.Client/Components/ModalResultView.razor`:

```razor
@using PoMode.Shared.Analysis

<RadzenCard>
    <RadzenText TextStyle="TextStyle.H6" Text="@($"Key: {Result.TonicName} · {PrimaryText}")" />
    <RadzenText class="tempo-note" Text="@($"Tempo {Result.TempoBpm:0} BPM (estimated) · {Result.Windows.Count} windows")" />
    <ul class="windows">
        @foreach (var window in Result.Windows)
        {
            <li class="window">
                <span class="measure">m@(window.MeasureNumber)</span>
                <span class="chord">@window.ChordSymbol</span>
                <span class="mode">@ModeText(window)</span>
                <span class="degrees">@string.Join(" ", window.SungIntervals.Select(Degree))</span>
            </li>
        }
    </ul>
</RadzenCard>

@code {
    private static readonly string[] Degrees = ["1", "b2", "2", "b3", "3", "4", "#4", "5", "b6", "6", "b7", "7"];

    [Parameter, EditorRequired]
    public required ModalResult Result { get; set; }

    private string PrimaryText => Result.PrimaryMode is null
        ? "mode unclear"
        : $"{Result.PrimaryMode} ({Result.PrimaryConfidence:P0})";

    private static string Degree(int semitones) => Degrees[((semitones % 12) + 12) % 12];

    private static string ModeText(ModalWindow window) => window.InsufficientEvidence || window.Matches.Count == 0
        ? "not enough notes"
        : $"{window.Matches[0].Mode} ({window.Matches[0].Confidence:P0})";
}
```

`src/PoMode.Client/Components/ModalResultView.razor.css`:
```css
.tempo-note {
    color: var(--pm-fg-muted);
    font-size: 0.85rem;
}

.windows {
    list-style: none;
    margin: 0.5rem 0 0;
    padding: 0;
}

.window {
    display: grid;
    grid-template-columns: 3rem 5rem 1fr 1fr;
    gap: 0.5rem;
    padding: 0.2rem 0;
    border-bottom: 1px solid var(--pm-border);
}

.measure {
    color: var(--pm-fg-muted);
}

.chord {
    font-weight: 600;
}

.mode {
    color: var(--pm-accent);
}

.degrees {
    color: var(--pm-fg-muted);
    font-family: monospace;
}
```

- [ ] **Step 3: Wire into `Home.razor`**

Add fields `private ModalResult? _result;` and reset `_result = null;` beside `_notes = null;` in `OnUploadComplete`. In `OnStatusChanged`, after fetching notes and chords, add `_result = await Analysis.GetResultAsync(status.JobId);`. Render after the results card:

```razor
@if (_result is not null)
{
    <ModalResultView Result="_result" />
    <RadzenLink Path="@($"api/analysis/{_status!.JobId}/midi")" Text="Download MIDI" Target="_blank" />
}
```

- [ ] **Step 4: Enable the header button** — in `MainLayout.razor` replace the disabled Export MIDI button with a `CascadingValue`-free simple approach: leave the header button disabled (it has no job context) and instead rely on the Home-page link. Change its text to `Export MIDI (on results)` so it stops looking broken. Keep zero inline CSS.

- [ ] **Step 5: Write the browser test** — `tests/PoMode.E2EUI/ModalResultTests.cs`:

```csharp
using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class ModalResultTests(AppFixture app)
{
    [Fact]
    public async Task Results_show_key_mode_and_a_midi_link()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-modal-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, TestAudio.MakeWav(seconds: 0.5));
        try
        {
            var visible = new LocatorAssertionsToBeVisibleOptions { Timeout = 30000f };
            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);

            await Assertions.Expect(page.GetByText("Analysis complete")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("Key:")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("(estimated)")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("Download MIDI")).ToBeVisibleAsync(visible);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }
}
```

- [ ] **Step 6: GREEN** — `dotnet test` (tee `task-8-full.log`). Expected: 107 passed, zero warnings.

- [ ] **Step 7: Commit**

```powershell
git add src/PoMode.Client tests/PoMode.E2EUI/ModalResultTests.cs
git commit -m "feat: display modal analysis and offer MIDI download in the client"
```

---

## Phase 3 Exit Criteria

- `dotnet test` green across all four projects; zero build warnings; no `Version=` in any csproj; no secrets; `.claude/` ignored.
- Manual: `dotnet run --project src/PoMode.API` → upload a track → results card shows `Key: C · Ionian (…)`, per-window chords with modes and scale degrees, and a working **Download MIDI** link whose file opens in a DAW with four tracks.
- `PlaceholderModalAnalyzer` is gone; stage 4 runs the real engine; `result.json` is served at `/api/analysis/{id}/result`.
- Every tempo-derived value is labelled "(estimated)" until Phase 4 replaces the fixed 120 BPM.
- Phase 4 starts from: `ModalAnalysisEngine.Analyze(notes, chords, tempoBpm)` — pass a real BPM; `ModalResult.TempoEstimated` flips to `false`; `MidiFileBuilder` needs no change.
