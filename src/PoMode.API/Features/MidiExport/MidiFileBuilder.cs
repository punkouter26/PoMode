using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
// Melanchall.DryWetMidi.Core also defines a NoteEvent (base class of NoteOnEvent/NoteOffEvent),
// which collides with PoMode.Shared.Analysis.NoteEvent. Alias ours to disambiguate.
using VocalNoteEvent = PoMode.Shared.Analysis.NoteEvent;

namespace PoMode.API.Features.MidiExport;

/// <summary>Builds a Standard MIDI File (Type 1) per spec §8. Tempo comes from the analysis result.</summary>
public static class MidiFileBuilder
{
    private const short TicksPerQuarter = 480;
    private const int VocalProgram = 80; // GM Lead 1 (square)
    private const int ChordProgram = 0;  // GM Acoustic Grand Piano

    public static byte[] Build(
        IReadOnlyList<VocalNoteEvent> notes,
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

    private static TrackChunk BuildVocalTrack(IReadOnlyList<VocalNoteEvent> notes, double ticksPerSecond)
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
        // The one chord-symbol → pitches decision lives in ChordPadBuilder; this track only
        // re-times its notes into ticks, so the export and the mixer pad can never drift.
        var events = new List<(long Tick, MidiEvent Event)>();
        foreach (var note in ChordPadBuilder.Build(chords))
        {
            var start = (long)Math.Round(note.StartSec * ticksPerSecond);
            var end = (long)Math.Round((note.StartSec + note.DurationSec) * ticksPerSecond);
            var pitch = (SevenBitNumber)Math.Clamp(note.MidiPitch, 0, 127);
            events.Add((start, new NoteOnEvent(pitch, (SevenBitNumber)Math.Clamp(note.Velocity, 1, 127))));
            events.Add((end, new NoteOffEvent(pitch, (SevenBitNumber)0)));
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
