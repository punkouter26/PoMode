using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

/// <summary>
/// A mode is a tonal centre, not just a note set. These cover the two things that have to hold for a
/// mode card to actually sound like its mode: the harmony counts from the mode's own tonic, and the
/// cadence stays inside the notes the melody is allowed to use.
/// </summary>
public class ModeSignatureHarmonyTests
{
    private readonly ModalMelodyGenerator _generator = new();

    /// <summary>The seven modes drawn from one parent scale. The pentatonics sit outside this set
    /// because a five-note scale is a melodic subset, so its triads legitimately reach past it.</summary>
    private static readonly ScaleMode[] DiatonicModes =
    [
        ScaleMode.Ionian, ScaleMode.Dorian, ScaleMode.Phrygian, ScaleMode.Lydian,
        ScaleMode.Mixolydian, ScaleMode.Aeolian, ScaleMode.Locrian,
    ];

    private static readonly ScaleMode[] EveryStripMode =
    [
        .. DiatonicModes, ScaleMode.MajorPentatonic, ScaleMode.MinorPentatonic,
    ];

    private static ModalMelodyRequest Request(ScaleMode mode, string progressionId) => new(
        TonicPitchClass: 0,
        Mode: mode,
        ProgressionId: progressionId,
        Bpm: 100.0,
        Style: MelodyStyle.Lyrical,
        Seed: 42,
        Octave: 4,
        TargetPurity: 90.0);

    private static int[] PitchClassesOf(ChordSpan chord)
    {
        Assert.True(PitchNames.TryParseRoot(chord.Root, out var rootClass), $"unparsable root {chord.Root}");
        return [.. ChordPadBuilder.VoicingFor(chord.Quality).Select(iv => (rootClass + iv) % 12)];
    }

    [Fact]
    public void Every_mode_on_the_strip_has_a_signature_cadence()
    {
        var catalog = _generator.GetProgressions();

        foreach (var mode in EveryStripMode)
        {
            Assert.True(catalog.SignatureFor(mode) is not null, $"{mode} has no signature cadence");
        }
    }

    [Fact]
    public void Signature_cadences_are_rooted_on_the_mode_not_the_parent_key()
    {
        foreach (var mode in EveryStripMode)
        {
            var signature = _generator.GetProgressions().SignatureFor(mode)!;
            var result = _generator.Generate(Request(mode, signature.Id));

            // Parent key is C throughout, so the mode root is what the degree offset lands on.
            var expectedRoot = ScaleModes.ModeDegreeOffset(mode) % 12;
            Assert.True(PitchNames.TryParseRoot(result.Chords[0].Root, out var firstChordRoot));
            Assert.True(
                firstChordRoot == expectedRoot,
                $"{mode}: cadence opens on {result.Chords[0].Symbol}, expected the mode root " +
                $"{ScaleModes.NoteName(expectedRoot)} — rooting on the parent key is the bug this guards");
        }
    }

    [Fact]
    public void Diatonic_signature_cadences_use_only_notes_the_mode_owns()
    {
        foreach (var mode in DiatonicModes)
        {
            var signature = _generator.GetProgressions().SignatureFor(mode)!;
            var result = _generator.Generate(Request(mode, signature.Id));
            var modeRoot = ScaleModes.ModeDegreeOffset(mode) % 12;
            var allowed = ScaleModes.Intervals(mode).Select(iv => (modeRoot + iv) % 12).ToHashSet();

            foreach (var chord in result.Chords)
            {
                foreach (var pitchClass in PitchClassesOf(chord))
                {
                    Assert.True(
                        allowed.Contains(pitchClass),
                        $"{mode}: {chord.Symbol} sounds {ScaleModes.NoteName(pitchClass)}, " +
                        $"which is not in {string.Join(" ", result.ScaleNotes)}");
                }
            }
        }
    }

    [Fact]
    public void Melodies_never_leave_the_mode_they_claim()
    {
        foreach (var mode in EveryStripMode)
        {
            var signature = _generator.GetProgressions().SignatureFor(mode)!;
            var result = _generator.Generate(Request(mode, signature.Id));
            var modeRoot = ScaleModes.ModeDegreeOffset(mode) % 12;
            var allowed = ScaleModes.Intervals(mode).Select(iv => (modeRoot + iv) % 12).ToHashSet();

            foreach (var note in result.MelodyNotes)
            {
                var pitchClass = ((note.MidiPitch % 12) + 12) % 12;
                Assert.True(
                    allowed.Contains(pitchClass),
                    $"{mode}: melody sounds {ScaleModes.NoteName(pitchClass)}, outside {string.Join(" ", result.ScaleNotes)}");
            }
        }
    }

    [Fact]
    public void Switching_mode_moves_the_harmony_so_the_modes_do_not_all_sound_alike()
    {
        var openingChords = EveryStripMode
            .Select(mode => _generator.Generate(Request(mode, _generator.GetProgressions().SignatureFor(mode)!.Id)))
            .Select(result => result.Chords[0].Symbol)
            .ToList();

        // Seven distinct tonal centres for the seven diatonic modes; the pentatonics reuse two of them.
        Assert.True(openingChords.Distinct().Count() >= 7,
            $"expected each mode to assert its own home chord, got: {string.Join(", ", openingChords)}");
    }

    [Fact]
    public void A_parent_rooted_progression_still_counts_from_the_parent_key()
    {
        // The pop presets are written as degrees of the parent major key, and must stay that way.
        var result = _generator.Generate(Request(ScaleMode.Dorian, "pop-axis"));

        Assert.Equal("C", result.Chords[0].Root);
        Assert.Equal(["C", "G", "Am", "F"], result.Chords.Select(c => c.Symbol).ToArray());
    }
}
