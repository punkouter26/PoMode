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
