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

    // ---- Role rules: one test per role, plus the precedence rule ----

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
    public void A_sharp_fourth_over_a_lydian_window_is_characteristic()
    {
        // Tonic C, Lydian characteristic degree is [6] = F#.
        var payload = VisualizationBuilder.Build(
            [Note(66)], // F#
            [Chord("C", "C", "maj", 0.0, 4.0)],
            ResultWith(ScaleMode.Lydian));

        Assert.Equal(NoteRole.Characteristic, RoleOf(payload, 66));
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

    // ---- Window lookup ----

    [Fact]
    public void A_boundary_time_belongs_to_the_later_window()
    {
        var first = new ModalWindow(0, 0.0, 2.0, "C", 1, 0, [], false, [new ModalMatch(ScaleMode.Ionian, 0.9, [], [])]);
        var second = new ModalWindow(1, 2.0, 4.0, "G", 1, 0, [], false, [new ModalMatch(ScaleMode.Ionian, 0.9, [], [])]);
        var result = ResultWith(ScaleMode.Ionian) with { Windows = [first, second] };

        Assert.Equal(1, result.WindowIndexAt(2.0));
    }
}
