using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.Visualization;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Visualization;

public class VisualizationBuilderTests
{
    private const int TonicC = 0;

    private static NoteEvent Note(int midiPitch, double startSec = 0.0, double durationSec = 0.5)
        => new(midiPitch, startSec, durationSec, Velocity: 90);

    private static ChordSpan Chord(string symbol, string root, string quality, double startSec, double endSec)
        => new(symbol, root, quality, startSec, endSec);

    /// <summary>A single window whose top-ranked mode is <paramref name="mode"/>.</summary>
    private static ModalResult ResultWith(
        ScaleMode? topMode,
        ScaleMode? primaryMode = null,
        double startSec = 0.0,
        double endSec = 4.0,
        bool insufficient = false,
        int tonicPitchClass = TonicC)
    {
        IReadOnlyList<ModalMatch> matches = topMode is null
            ? []
            : [new ModalMatch(topMode.Value, 0.9, [0, 4, 7], [])];

        var window = new ModalWindow(
            Index: 0,
            StartSec: startSec,
            EndSec: endSec,
            ChordSymbol: "C",
            MeasureNumber: 1,
            VocalMask: 0b10010001,
            SungIntervals: [0, 4, 7],
            InsufficientEvidence: insufficient,
            Matches: matches);

        return new ModalResult(
            SchemaVersion: 1,
            TonicPitchClass: tonicPitchClass,
            TonicName: PitchNames.Name(tonicPitchClass),
            TonicConfidence: 0.8,
            PrimaryMode: primaryMode ?? topMode,
            PrimaryConfidence: 0.85,
            TempoBpm: 120.0,
            TempoEstimated: false,
            Windows: [window]);
    }

    private static NoteRole RoleOf(VisualizationPayload payload, int midiPitch)
        => payload.Notes.Single(note => note.MidiPitch == midiPitch).Role;

    // ---- Role rule 1: chord tones win ----

    [Theory]
    [InlineData(60)] // C
    [InlineData(64)] // E
    [InlineData(67)] // G
    public void Triad_members_of_the_covering_chord_are_chord_tones(int midiPitch)
    {
        var payload = VisualizationBuilder.Build(
            [Note(midiPitch)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Ionian));

        Assert.Equal(NoteRole.ChordTone, RoleOf(payload, midiPitch));
    }

    [Fact]
    public void A_minor_chord_uses_the_minor_third_not_the_major_third()
    {
        var payload = VisualizationBuilder.Build(
            [Note(63), Note(64)], // Eb is in Cm, E is not
            [Chord("Cm", "C", "min", 0.0, 4.0)],
            ResultWith(ScaleMode.Aeolian));

        Assert.Equal(NoteRole.ChordTone, RoleOf(payload, 63));
        Assert.NotEqual(NoteRole.ChordTone, RoleOf(payload, 64));
    }

    [Fact]
    public void Chord_tone_beats_characteristic_when_a_note_is_both()
    {
        // Tonic C, Mixolydian's characteristic degrees include the major 3rd (4).
        // E is also the third of a C major chord, so ChordTone must win.
        var payload = VisualizationBuilder.Build(
            [Note(64)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Mixolydian));

        Assert.Equal(NoteRole.ChordTone, RoleOf(payload, 64));
    }

    [Fact]
    public void Notes_outside_every_chord_span_are_still_classified_by_mode()
    {
        var payload = VisualizationBuilder.Build(
            [Note(62, startSec: 9.0)], // D, past the only chord
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Ionian));

        Assert.Equal(NoteRole.InMode, RoleOf(payload, 62));
    }

    // ---- Role rule 2: characteristic degrees ----

    [Fact]
    public void A_natural_sixth_over_a_dorian_window_is_characteristic_not_merely_in_mode()
    {
        // Tonic C, Dorian characteristic degrees are [9, 3]. A (=9) must be Characteristic.
        // The covering chord is Dm so A is deliberately NOT one of its triad tones... it is
        // the fifth of Dm, so use Gm instead: G Bb D contains no A.
        var payload = VisualizationBuilder.Build(
            [Note(69)], // A
            [Chord("Gm", "G", "min", 0.0, 4.0)],
            ResultWith(ScaleMode.Dorian));

        Assert.Equal(NoteRole.Characteristic, RoleOf(payload, 69));
    }

    [Fact]
    public void A_sharp_fourth_over_a_lydian_window_is_characteristic()
    {
        // Tonic C, Lydian characteristic degree is [6] = F#.
        var payload = VisualizationBuilder.Build(
            [Note(66)], // F#
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Lydian));

        Assert.Equal(NoteRole.Characteristic, RoleOf(payload, 66));
    }

    // ---- Role rules 3 and 4 ----

    [Fact]
    public void A_plain_scale_degree_is_in_mode()
    {
        // Tonic C, Ionian. D (=2) is in the mode and is not a characteristic degree ([11]).
        var payload = VisualizationBuilder.Build(
            [Note(62)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Ionian));

        Assert.Equal(NoteRole.InMode, RoleOf(payload, 62));
    }

    [Fact]
    public void A_chromatic_note_outside_the_mode_is_outside()
    {
        // Tonic C, Ionian mask has no 1 (Db) and Db is not in a C major triad.
        var payload = VisualizationBuilder.Build(
            [Note(61)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Ionian));

        Assert.Equal(NoteRole.Outside, RoleOf(payload, 61));
    }

    [Fact]
    public void Roles_are_measured_against_the_tonic_not_against_c()
    {
        // Tonic D. F# is interval 4 above D, so it is in D Ionian; against C it would be a #4.
        var payload = VisualizationBuilder.Build(
            [Note(66)],
            [Chord("Gm", "G", "min", 0.0, 4.0)],
            ResultWith(ScaleMode.Ionian, tonicPitchClass: 2));

        Assert.Equal(NoteRole.InMode, RoleOf(payload, 66));
    }

    // ---- Fallbacks ----

    [Fact]
    public void An_insufficient_evidence_window_falls_back_to_the_primary_mode()
    {
        var payload = VisualizationBuilder.Build(
            [Note(66)], // F# — outside Ionian, inside Lydian
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(topMode: null, primaryMode: ScaleMode.Lydian, insufficient: true));

        Assert.Equal(NoteRole.Characteristic, RoleOf(payload, 66));
    }

    [Fact]
    public void With_no_mode_at_all_non_chord_tones_are_outside_and_nothing_throws()
    {
        var payload = VisualizationBuilder.Build(
            [Note(60), Note(61)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(topMode: null, primaryMode: null, insufficient: true));

        Assert.Equal(NoteRole.ChordTone, RoleOf(payload, 60));
        Assert.Equal(NoteRole.Outside, RoleOf(payload, 61));
    }

    // ---- Labels ----

    [Theory]
    [InlineData(60, "C4")]
    [InlineData(69, "A4")]
    [InlineData(66, "F#4")]
    [InlineData(48, "C3")]
    public void Pitch_labels_are_scientific_pitch(int midiPitch, string expected)
    {
        var payload = VisualizationBuilder.Build(
            [Note(midiPitch)], [Chord("C", "C", "maj", 0.0, 4.0)], ResultWith(ScaleMode.Ionian));

        Assert.Equal(expected, payload.Notes.Single().PitchLabel);
    }

    [Theory]
    [InlineData(60, "[1]")]
    [InlineData(63, "[b3]")]
    [InlineData(65, "[4]")]
    [InlineData(66, "[#4]")]
    public void Degree_labels_are_bracketed_and_relative_to_the_tonic(int midiPitch, string expected)
    {
        var payload = VisualizationBuilder.Build(
            [Note(midiPitch)], [Chord("C", "C", "maj", 0.0, 4.0)], ResultWith(ScaleMode.Ionian));

        Assert.Equal(expected, payload.Notes.Single().DegreeLabel);
    }

    // ---- Chord blocks ----

    [Fact]
    public void Chord_blocks_carry_the_measure_number_and_mode_tag_from_their_window()
    {
        var payload = VisualizationBuilder.Build(
            [Note(60)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Dorian));

        var block = Assert.Single(payload.Chords);
        Assert.Equal("C", block.Symbol);
        Assert.Equal(1, block.MeasureNumber);
        Assert.Equal("Dorian", block.ModeTag);
    }

    [Fact]
    public void A_chord_with_no_usable_window_has_a_null_mode_tag()
    {
        var payload = VisualizationBuilder.Build(
            [Note(60)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(topMode: null, primaryMode: null, insufficient: true));

        Assert.Null(Assert.Single(payload.Chords).ModeTag);
    }

    // ---- Extents ----

    [Fact]
    public void Duration_is_the_later_of_the_last_note_end_and_the_last_chord_end()
    {
        var longNotes = VisualizationBuilder.Build(
            [Note(60, startSec: 3.0, durationSec: 5.0)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Ionian));
        Assert.Equal(8.0, longNotes.DurationSec, precision: 6);

        var longChords = VisualizationBuilder.Build(
            [Note(60, startSec: 0.0, durationSec: 1.0)],
            [Chord("C", "C", "maj", 0.0, 12.0)],
            ResultWith(ScaleMode.Ionian));
        Assert.Equal(12.0, longChords.DurationSec, precision: 6);
    }

    [Fact]
    public void The_pitch_range_brackets_the_notes()
    {
        var payload = VisualizationBuilder.Build(
            [Note(55), Note(79)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Ionian));

        Assert.True(payload.MinPitch <= 55, $"MinPitch was {payload.MinPitch}");
        Assert.True(payload.MaxPitch >= 79, $"MaxPitch was {payload.MaxPitch}");
    }

    [Fact]
    public void The_pitch_range_stays_usable_for_an_empty_or_single_pitch_track()
    {
        var empty = VisualizationBuilder.Build([], [], ResultWith(topMode: null, primaryMode: null));
        Assert.True(empty.MaxPitch - empty.MinPitch >= 12, $"range was {empty.MaxPitch - empty.MinPitch}");
        Assert.Equal(0.0, empty.DurationSec);
        Assert.Empty(empty.Notes);
        Assert.Empty(empty.Chords);

        var single = VisualizationBuilder.Build(
            [Note(60)], [Chord("C", "C", "maj", 0.0, 4.0)], ResultWith(ScaleMode.Ionian));
        Assert.True(single.MaxPitch - single.MinPitch >= 12, $"range was {single.MaxPitch - single.MinPitch}");
    }

    // ---- Window lookup ----

    [Fact]
    public void Window_lookup_finds_the_covering_window()
    {
        var result = ResultWith(ScaleMode.Ionian, startSec: 0.0, endSec: 4.0);

        Assert.Equal(0, VisualizationBuilder.WindowIndexAt(result, 0.0));
        Assert.Equal(0, VisualizationBuilder.WindowIndexAt(result, 3.99));
    }

    [Fact]
    public void A_boundary_time_belongs_to_the_later_window()
    {
        var first = new ModalWindow(0, 0.0, 2.0, "C", 1, 0, [], false, [new ModalMatch(ScaleMode.Ionian, 0.9, [], [])]);
        var second = new ModalWindow(1, 2.0, 4.0, "G", 1, 0, [], false, [new ModalMatch(ScaleMode.Ionian, 0.9, [], [])]);
        var result = ResultWith(ScaleMode.Ionian) with { Windows = [first, second] };

        Assert.Equal(1, VisualizationBuilder.WindowIndexAt(result, 2.0));
    }

    [Fact]
    public void Window_lookup_returns_null_past_the_end_and_before_the_start()
    {
        var result = ResultWith(ScaleMode.Ionian, startSec: 1.0, endSec: 4.0);

        Assert.Null(VisualizationBuilder.WindowIndexAt(result, 4.0));
        Assert.Null(VisualizationBuilder.WindowIndexAt(result, 0.5));
    }

    // ---- End to end through the real engine ----

    [Fact]
    public void It_builds_from_a_real_modal_analysis_without_throwing()
    {
        List<NoteEvent> notes =
        [
            Note(60, 0.0), Note(62, 0.5), Note(64, 1.0), Note(65, 1.5),
            Note(67, 2.0), Note(69, 2.5), Note(71, 3.0),
        ];
        List<ChordSpan> chords = [Chord("C", "C", "maj", 0.0, 2.0), Chord("G", "G", "maj", 2.0, 4.0)];
        var result = ModalAnalysisEngine.Analyze(notes, chords, tempoBpm: 120.0, tempoEstimated: false);

        var payload = VisualizationBuilder.Build(notes, chords, result);

        Assert.Equal(notes.Count, payload.Notes.Count);
        Assert.Equal(chords.Count, payload.Chords.Count);
        Assert_all_labels_present(payload);
        Assert.Equal(1, payload.SchemaVersion);
    }

    private static void Assert_all_labels_present(VisualizationPayload payload)
    {
        Assert.All(payload.Notes, note =>
        {
            Assert.False(string.IsNullOrWhiteSpace(note.PitchLabel));
            Assert.StartsWith("[", note.DegreeLabel);
            Assert.EndsWith("]", note.DegreeLabel);
        });
    }
}
