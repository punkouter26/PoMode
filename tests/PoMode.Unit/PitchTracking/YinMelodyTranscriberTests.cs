using PoMode.API.Features.Audio;
using PoMode.API.Features.PitchTracking;
using Xunit;

namespace PoMode.Unit.PitchTracking;

public class YinMelodyTranscriberTests
{
    private const int SampleRate = 22050;

    private static float[] Sine(double frequencyHz, double seconds, double amplitude = 0.5)
    {
        var samples = new float[(int)(SampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / SampleRate));
        }
        return samples;
    }

    [Fact]
    public void A_pure_A4_tone_transcribes_to_a_single_midi_69_note()
    {
        var buffer = new AudioBuffer(Sine(440.0, 1.5), SampleRate, 1);

        var notes = YinMelodyTranscriber.Transcribe(buffer);

        var note = Assert.Single(notes);
        Assert.Equal(69, note.MidiPitch);
        Assert.True(note.DurationSec > 1.0, $"expected a sustained note, got {note.DurationSec:0.00}s");
        Assert.InRange(note.Velocity, 1, 127);
    }

    [Fact]
    public void Two_tones_in_sequence_transcribe_to_two_notes_in_order()
    {
        var first = Sine(220.0, 1.0);  // A3 = MIDI 57
        var second = Sine(329.63, 1.0); // E4 = MIDI 64
        var samples = new float[first.Length + second.Length];
        first.CopyTo(samples, 0);
        second.CopyTo(samples, first.Length);
        var buffer = new AudioBuffer(samples, SampleRate, 1);

        var notes = YinMelodyTranscriber.Transcribe(buffer);

        // The boundary frame may flicker and be dropped by the minimum duration, so assert the
        // two sustained notes rather than an exact count of artifacts.
        Assert.Contains(notes, n => n.MidiPitch == 57 && n.DurationSec > 0.5);
        Assert.Contains(notes, n => n.MidiPitch == 64 && n.DurationSec > 0.5);
        Assert.True(
            notes.First(n => n.MidiPitch == 57).StartSec < notes.First(n => n.MidiPitch == 64).StartSec,
            "the A3 note must start before the E4 note");
    }

    [Fact]
    public void Silence_transcribes_to_no_notes()
    {
        var buffer = new AudioBuffer(new float[SampleRate * 2], SampleRate, 1);

        var notes = YinMelodyTranscriber.Transcribe(buffer);

        Assert.Empty(notes);
    }
}
