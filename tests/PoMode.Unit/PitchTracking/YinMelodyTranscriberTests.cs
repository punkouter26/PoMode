using PoMode.API.Audio;
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
    public void A_pure_A4_tone_is_one_midi_69_note_measured_to_the_cent_and_silence_is_none()
    {
        var buffer = new AudioBuffer(Sine(440.0, 1.5), SampleRate, 1);

        var notes = YinMelodyTranscriber.Transcribe(buffer);

        var note = Assert.Single(notes);
        Assert.Equal(69, note.MidiPitch);
        Assert.True(note.DurationSec > 1.0, $"expected a sustained note, got {note.DurationSec:0.00}s");
        Assert.InRange(note.Velocity, 1, 127);

        // Inside a note, the continuous pitch is what intonation is graded on: a tone 20 cents sharp
        // (445.1 Hz) must read as 69.2, not round back to 69.
        var sharp = new AudioBuffer(Sine(445.1, 1.5), SampleRate, 1);
        var inside = Assert.Single(YinMelodyTranscriber.PitchesInside(sharp, [(0.0, 1.5)]));
        Assert.NotEmpty(inside);
        Assert.All(inside, midi => Assert.InRange(midi, 69.17, 69.23));

        var silence = new AudioBuffer(new float[SampleRate * 2], SampleRate, 1);
        Assert.Empty(YinMelodyTranscriber.Transcribe(silence));
        Assert.Empty(Assert.Single(YinMelodyTranscriber.PitchesInside(silence, [(0.0, 2.0)])));
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
}
