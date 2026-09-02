using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.Visualization;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Visualization;

public class VisualizationBuilderTests
{
    private static NoteEvent Note(int midiPitch, double startSec = 0.0, double durationSec = 0.5)
        => new(midiPitch, startSec, durationSec, Velocity: 90);

    private static ChordSpan Chord(string symbol, string root, string quality, double startSec, double endSec)
        => new(symbol, root, quality, startSec, endSec);

    private static ModalResult ResultWith(ScaleMode? topMode, int tonic = 0)
    {
        IReadOnlyList<ModalMatch> matches = topMode is null
            ? []
            : [new ModalMatch(topMode.Value, 0.9, [0, 4, 7], [])];

        var window = new ModalWindow(0, 0.0, 4.0, "C", 1, 0b10010001, [0, 4, 7], false, matches);

        return new ModalResult(1, tonic, PitchNames.Name(tonic), 0.8, topMode, 0.85, 120.0, false, [window]);
    }

    [Fact]
    public void RolePrecedence_ChordToneBeatsCharacteristicAndAssignsRoles()
    {
        // In C Major (Ionian), note C (60) over chord C is a ChordTone
        var chords = new[] { Chord("C", "C", "maj", 0.0, 4.0) };
        var result = ResultWith(ScaleMode.Ionian, tonic: 0);

        var payload = VisualizationBuilder.Build([Note(60)], chords, result);
        Assert.Equal(NoteRole.ChordTone, payload.Notes.Single().Role);

        // Characteristic Note (B = pitch 71 in D Dorian over C chord)
        var chordsC = new[] { Chord("C", "C", "maj", 0.0, 4.0) };
        var dorianResult = new ModalResult(1, 2, "D", 0.8, ScaleMode.Dorian, 0.85, 120.0, false, [
            new ModalWindow(0, 0.0, 4.0, "C", 1, 0, [0, 9], false, [new ModalMatch(ScaleMode.Dorian, 0.9, [0, 9], [])])
        ]);
        var dorianPayload = VisualizationBuilder.Build([Note(71)], chordsC, dorianResult);
        Assert.Equal(NoteRole.Characteristic, dorianPayload.Notes.Single().Role);
    }

    [Fact]
    public void InModeAndOutsideRoles_AreAssignedCorrectly()
    {
        var chords = new[] { Chord("C", "C", "maj", 0.0, 4.0) };
        var ionianResult = ResultWith(ScaleMode.Ionian, tonic: 0);

        // In Mode (D in C Ionian over C chord)
        var inModePayload = VisualizationBuilder.Build([Note(62)], chords, ionianResult);
        Assert.Equal(NoteRole.InMode, inModePayload.Notes.Single().Role);

        // Outside (C# in C Ionian over C chord)
        var outsidePayload = VisualizationBuilder.Build([Note(61)], chords, ionianResult);
        Assert.Equal(NoteRole.Outside, outsidePayload.Notes.Single().Role);
    }

    [Fact]
    public void PayloadStructure_IncludesWindowsAndNotes()
    {
        var chords = new[] { Chord("C", "C", "maj", 0.0, 4.0) };
        var result = ResultWith(ScaleMode.Ionian, tonic: 0);
        var payload = VisualizationBuilder.Build([Note(60)], chords, result);

        Assert.NotEmpty(payload.Notes);
        Assert.NotEmpty(payload.Chords);
        Assert.NotEmpty(payload.Windows);
        Assert.Equal("C4", payload.Notes[0].PitchLabel);
    }
}
