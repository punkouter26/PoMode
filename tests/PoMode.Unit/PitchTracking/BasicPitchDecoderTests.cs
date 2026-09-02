using PoMode.API.Features.PitchTracking;
using Xunit;

namespace PoMode.Unit.PitchTracking;

public class BasicPitchDecoderTests
{
    private const double Fps = 100.0;
    private const int MinMidi = 21;

    private static (float[,] Onsets, float[,] Frames) Empty(int frames, int pitches)
        => (new float[frames, pitches], new float[frames, pitches]);

    [Fact]
    public void Sustained_note_decodes_correctly()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 0.9f;
        for (var f = 10; f < 40; f++) frames[f, bin] = 0.8f;

        var notes = BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi);
        var note = Assert.Single(notes);
        Assert.Equal(60, note.MidiPitch);
        Assert.InRange(note.StartSec, 0.09, 0.11);
        Assert.InRange(note.DurationSec, 0.28, 0.32);
    }

    [Fact]
    public void Thresholds_drop_short_and_weak_notes()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 0.9f;
        frames[10, bin] = 0.8f; // too short
        Assert.Empty(BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi));

        var (onsets2, frames2) = Empty(100, 88);
        onsets2[10, bin] = 0.2f; // weak onset
        for (var f = 10; f < 40; f++) frames2[f, bin] = 0.8f;
        Assert.Empty(BasicPitchDecoder.Decode(onsets2, frames2, Fps, MinMidi));
    }

    [Fact]
    public void Multiple_notes_decode_in_strict_time_order()
    {
        var (onsets, frames) = Empty(300, 88);
        foreach (var (start, pitch) in new[] { (100, 67), (10, 60), (200, 64) })
        {
            var bin = pitch - MinMidi;
            onsets[start, bin] = 0.9f;
            for (var f = start; f < start + 20; f++) frames[f, bin] = 0.8f;
        }

        var notes = BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi);
        Assert.Equal(3, notes.Count);
        Assert.Equal(notes.OrderBy(n => n.StartSec).Select(n => n.MidiPitch), notes.Select(n => n.MidiPitch));
    }
}
