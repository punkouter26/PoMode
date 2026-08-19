using PoMode.API.Features.PitchTracking;
using Xunit;

namespace PoMode.Unit.PitchTracking;

public class BasicPitchDecoderTests
{
    private const double Fps = 100.0; // 10 ms per frame
    private const int MinMidi = 21;

    private static (float[,] Onsets, float[,] Frames) Empty(int frames, int pitches)
        => (new float[frames, pitches], new float[frames, pitches]);

    [Fact]
    public void A_sustained_note_becomes_one_event_with_the_right_pitch_and_duration()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 0.9f;
        for (var f = 10; f < 40; f++)
        {
            frames[f, bin] = 0.8f;
        }

        var notes = BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi);

        var note = Assert.Single(notes);
        Assert.Equal(60, note.MidiPitch);
        Assert.InRange(note.StartSec, 0.09, 0.11);
        Assert.InRange(note.DurationSec, 0.28, 0.32);
    }

    [Fact]
    public void Notes_shorter_than_the_minimum_are_dropped()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 0.9f;
        frames[10, bin] = 0.8f; // a single 10 ms frame, under the 58 ms floor

        Assert.Empty(BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi));
    }

    [Fact]
    public void Weak_onsets_do_not_start_a_note()
    {
        var (onsets, frames) = Empty(100, 88);
        var bin = 60 - MinMidi;
        onsets[10, bin] = 0.2f; // below the 0.5 onset threshold
        for (var f = 10; f < 40; f++)
        {
            frames[f, bin] = 0.8f;
        }

        Assert.Empty(BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi));
    }

    [Fact]
    public void Two_separate_onsets_on_one_pitch_become_two_notes()
    {
        var (onsets, frames) = Empty(200, 88);
        var bin = 62 - MinMidi;
        onsets[10, bin] = 0.9f;
        for (var f = 10; f < 30; f++) frames[f, bin] = 0.8f;
        onsets[60, bin] = 0.9f;
        for (var f = 60; f < 90; f++) frames[f, bin] = 0.8f;

        var notes = BasicPitchDecoder.Decode(onsets, frames, Fps, MinMidi);

        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal(62, n.MidiPitch));
        Assert.True(notes[1].StartSec > notes[0].StartSec);
    }

    /// <summary>The canvas and mixer both virtualize over start-sorted notes, so ordering is a
    /// contract, not a nicety.</summary>
    [Fact]
    public void Notes_come_back_in_time_order()
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
