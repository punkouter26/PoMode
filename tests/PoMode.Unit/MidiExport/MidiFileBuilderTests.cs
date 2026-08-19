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
