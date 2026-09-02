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

    /// <summary>
    /// One walk of the strip asserting everything that has to hold for a card to sound like its mode:
    /// a cadence exists, it counts from the mode's own tonic, a diatonic mode's chords stay inside the
    /// notes it owns, the melody never leaves the mode it claims, and the modes do not all land on the
    /// same home chord. Kept as one sweep so a failure names the mode and the invariant together.
    /// </summary>
    [Fact]
    public void Every_mode_signature_puts_that_modes_own_tonic_at_home()
    {
        var catalog = _generator.GetProgressions();
        var openingChords = new List<string>();

        foreach (var mode in EveryStripMode)
        {
            var signature = catalog.SignatureFor(mode);
            Assert.True(signature is not null, $"{mode} has no signature cadence");

            var result = _generator.Generate(Request(mode, signature!.Id));
            var modeRoot = ScaleModes.ModeDegreeOffset(mode) % 12;
            var allowed = ScaleModes.Intervals(mode).Select(iv => (modeRoot + iv) % 12).ToHashSet();
            openingChords.Add(result.Chords[0].Symbol);

            // Parent key is C throughout, so the mode root is what the degree offset lands on.
            Assert.True(PitchNames.TryParseRoot(result.Chords[0].Root, out var firstChordRoot));
            Assert.True(
                firstChordRoot == modeRoot,
                $"{mode}: cadence opens on {result.Chords[0].Symbol}, expected the mode root " +
                $"{ScaleModes.NoteName(modeRoot)} — rooting on the parent key is the bug this guards");

            // A pentatonic is a melodic subset, so its triads legitimately reach past the five notes.
            if (DiatonicModes.Contains(mode))
            {
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

            foreach (var note in result.MelodyNotes)
            {
                var pitchClass = ((note.MidiPitch % 12) + 12) % 12;
                Assert.True(
                    allowed.Contains(pitchClass),
                    $"{mode}: melody sounds {ScaleModes.NoteName(pitchClass)}, outside {string.Join(" ", result.ScaleNotes)}");
            }
        }

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
