using System.Text.Json;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;
using PoMode.Shared.Serialization;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public sealed class ModalMelodyGeneratorTests
{
    private readonly ModalMelodyGenerator _generator = new();

    [Fact]
    public void GetProgressions_ReturnsStandardPresetLibrary()
    {
        var progressions = _generator.GetProgressions();

        Assert.NotEmpty(progressions);
        Assert.Contains(progressions, p => p.Id == "pop-axis");
        Assert.Contains(progressions, p => p.Id == "dorian-vamp");
        Assert.Contains(progressions, p => p.Id == "lydian-space");
        Assert.Contains(progressions, p => p.Id == "phrygian-tension");

        foreach (var prog in progressions)
        {
            Assert.False(string.IsNullOrWhiteSpace(prog.Id));
            Assert.False(string.IsNullOrWhiteSpace(prog.Name));
            Assert.False(string.IsNullOrWhiteSpace(prog.RomanNumerals));
            Assert.NotEmpty(prog.ChordFormulas);
            Assert.True(prog.DefaultBpm is >= 50 and <= 200);
        }
    }

    [Fact]
    public void Generate_AllModesStrictlyUseParentScaleNotesWithZeroOutsideNotes()
    {
        var modes = new[]
        {
            ScaleMode.Ionian, ScaleMode.Dorian, ScaleMode.Phrygian, ScaleMode.Lydian,
            ScaleMode.Mixolydian, ScaleMode.Aeolian, ScaleMode.Locrian,
            ScaleMode.MajorPentatonic, ScaleMode.MinorPentatonic
        };

        foreach (var mode in modes)
        {
            var request = new ModalMelodyRequest(
                TonicPitchClass: 0,
                Mode: mode,
                ProgressionId: "pop-axis",
                Bpm: 120.0,
                Style: MelodyStyle.Lyrical,
                Seed: 12345);

            var result = _generator.Generate(request);

            Assert.NotNull(result);
            Assert.Equal(mode, result.Mode);
            Assert.NotEmpty(result.BackingNotes);
            Assert.NotEmpty(result.Chords);
            Assert.True(result.ModePercentage is >= 50.0 and <= 100.0);

            var allowedPitchClasses = new[] { 0, 2, 4, 5, 7, 9, 11 }.ToHashSet();
            foreach (var note in result.MelodyNotes)
            {
                var pitchClass = ((note.MidiPitch % 12) + 12) % 12;
                Assert.Contains(pitchClass, allowedPitchClasses);
            }
        }
    }

    [Fact]
    public void Generate_FirstNoteStartsOnModeRoot()
    {
        var modeRoots = new[]
        {
            (ScaleMode.Ionian, 0), (ScaleMode.Dorian, 2), (ScaleMode.Phrygian, 4),
            (ScaleMode.Lydian, 5), (ScaleMode.Mixolydian, 7), (ScaleMode.Aeolian, 9), (ScaleMode.Locrian, 11)
        };

        foreach (var (mode, expectedRootClass) in modeRoots)
        {
            var request = new ModalMelodyRequest(
                TonicPitchClass: 0,
                Mode: mode,
                ProgressionId: "pop-axis",
                Bpm: 104.0,
                Style: MelodyStyle.Lyrical,
                Seed: 42);

            var result = _generator.Generate(request);
            Assert.NotEmpty(result.MelodyNotes);
            var firstNotePitchClass = ((result.MelodyNotes[0].MidiPitch % 12) + 12) % 12;
            Assert.Equal(expectedRootClass, firstNotePitchClass);
        }
    }

    [Fact]
    public void RelativeScaleNotes_StartOnRespectiveScaleDegree()
    {
        var modeRoots = new[]
        {
            (ScaleMode.Ionian, 0), (ScaleMode.Dorian, 2), (ScaleMode.Phrygian, 4),
            (ScaleMode.Lydian, 5), (ScaleMode.Mixolydian, 7), (ScaleMode.Aeolian, 9), (ScaleMode.Locrian, 11)
        };

        var cMajorNotes = new HashSet<string> { "C", "D", "E", "F", "G", "A", "B" };
        foreach (var (mode, expectedRootClass) in modeRoots)
        {
            var scaleNotes = ScaleModes.RelativeScaleNoteNames(0, mode);
            Assert.Equal(7, scaleNotes.Length);
            Assert.Equal(PitchNames.Name(expectedRootClass), scaleNotes[0]);

            foreach (var noteName in scaleNotes)
            {
                Assert.Contains(noteName, cMajorNotes);
            }
        }
    }

    [Fact]
    public void Generate_SupportsAllMelodicStyles()
    {
        var styles = new[] { MelodyStyle.Lyrical, MelodyStyle.Arpeggiated, MelodyStyle.Syncopated, MelodyStyle.Motific };
        foreach (var style in styles)
        {
            var request = new ModalMelodyRequest(
                TonicPitchClass: 0,
                Mode: ScaleMode.Mixolydian,
                ProgressionId: "mixolydian-rock",
                Bpm: 116.0,
                Style: style,
                Seed: 777);

            var result = _generator.Generate(request);
            Assert.NotEmpty(result.MelodyNotes);
            Assert.Equal(style, result.Style);
            Assert.NotNull(result.Visual);
            Assert.Equal(result.MelodyNotes.Count, result.Visual.Notes.Count);
        }
    }

    [Fact]
    public void GenerateComparison_ProducesAllModesWithValidData()
    {
        var comparison = _generator.GenerateComparison(
            tonicPitchClass: 0,
            progressionId: "pop-axis",
            bpm: 100.0,
            style: MelodyStyle.Lyrical,
            seed: 99);

        Assert.NotNull(comparison);
        Assert.Equal(9, comparison.Modes.Count);
        Assert.Contains(comparison.Modes, m => m.Mode == ScaleMode.Ionian);
        Assert.Contains(comparison.Modes, m => m.Mode == ScaleMode.Dorian);
        Assert.Contains(comparison.Modes, m => m.Mode == ScaleMode.Phrygian);
        Assert.Contains(comparison.Modes, m => m.Mode == ScaleMode.Lydian);
        Assert.Contains(comparison.Modes, m => m.Mode == ScaleMode.Mixolydian);
        Assert.Contains(comparison.Modes, m => m.Mode == ScaleMode.Aeolian);
        Assert.Contains(comparison.Modes, m => m.Mode == ScaleMode.Locrian);

        foreach (var item in comparison.Modes)
        {
            Assert.NotEmpty(item.MelodyNotes);
            Assert.NotEmpty(item.ScaleNotes);
            Assert.False(string.IsNullOrWhiteSpace(item.Mood));
            Assert.False(string.IsNullOrWhiteSpace(item.CharacteristicTone));
        }
    }

    [Fact]
    public void Serialization_ContractsRoundTripViaPoModeJsonContext()
    {
        var request = new ModalMelodyRequest(
            TonicPitchClass: 0,
            Mode: ScaleMode.Dorian,
            ProgressionId: "dorian-vamp",
            Bpm: 112.0,
            Style: MelodyStyle.Lyrical,
            Seed: 42);

        var result = _generator.Generate(request);

        var json = JsonSerializer.Serialize(result, PoModeJsonContext.Default.GeneratedMelodyDto);
        Assert.False(string.IsNullOrWhiteSpace(json));

        var roundTripped = JsonSerializer.Deserialize(json, PoModeJsonContext.Default.GeneratedMelodyDto);
        Assert.NotNull(roundTripped);
        Assert.Equal(result.TonicPitchClass, roundTripped.TonicPitchClass);
        Assert.Equal(result.Mode, roundTripped.Mode);
        Assert.Equal(result.ProgressionId, roundTripped.ProgressionId);
        Assert.Equal(result.MelodyNotes.Count, roundTripped.MelodyNotes.Count);
        Assert.Equal(result.Chords.Count, roundTripped.Chords.Count);
    }
}
