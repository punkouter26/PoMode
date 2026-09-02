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
    public void Builds_type1_midi_file_with_conductor_and_metadata()
    {
        var bytes = MidiFileBuilder.Build(
            [new(62, 0.0, 0.5, 96)],
            [new("Dm7", "D", "min7", 0, 2), new("G7", "G", "7", 2, 4)],
            Result(bpm: 90.0));

        var file = Parse(bytes);
        Assert.Equal(MidiFileFormat.MultiTrack, file.OriginalFormat);
        Assert.Equal(4, file.GetTrackChunks().Count());

        var conductor = file.GetTrackChunks().First();
        var tempo = conductor.Events.OfType<SetTempoEvent>().Single();
        Assert.Equal(90.0, 60_000_000.0 / tempo.MicrosecondsPerQuarterNote, precision: 1);
    }

    [Fact]
    public void Vocal_notes_and_markers_survive_midi_round_trip()
    {
        var bytes = MidiFileBuilder.Build(
            [new(62, 0.0, 0.5, 96)],
            [new("Dm7", "D", "min7", 0, 2)],
            Result());

        var file = Parse(bytes);
        var notes = file.GetNotes();
        Assert.Contains(notes, n => n.NoteNumber == 62);
    }
}
