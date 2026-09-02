using System.Buffers.Binary;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalMelodies;

/// <summary>
/// High-fidelity PCM WAV audio synthesizer that renders multi-track Concert Flute melody lead
/// and Acoustic Grand Piano chord accompaniment into a 44.1 kHz 16-bit stereo RIFF WAV.
/// </summary>
public static class ModalWavSynthesizer
{
    private const int SampleRate = 44100;
    private const int Channels = 2; // Stereo
    private const int BitsPerSample = 16;

    public static byte[] Synthesize(
        IReadOnlyList<NoteEvent> melodyNotes,
        IReadOnlyList<ChordSpan> chords,
        double totalDurationSec)
    {
        var duration = Math.Max(totalDurationSec + 1.0, 4.0);
        var totalSamples = (int)(duration * SampleRate);

        // Render buffers for Left and Right channels (floating-point -1.0 to 1.0)
        var leftBuffer = new float[totalSamples];
        var rightBuffer = new float[totalSamples];

        // 1. Render Backing Piano Chords
        RenderPianoChords(chords, leftBuffer, rightBuffer);

        // 2. Render Flute Melody Lead
        RenderFluteMelody(melodyNotes, leftBuffer, rightBuffer);

        // 3. Master Soft Limiting and Normalization
        MasterAudio(leftBuffer, rightBuffer);

        // 4. Encode to 16-bit Stereo PCM WAV
        return EncodePcmWav(leftBuffer, rightBuffer);
    }

    private static void RenderPianoChords(
        IReadOnlyList<ChordSpan> chords,
        float[] left,
        float[] right)
    {
        foreach (var chord in chords)
        {
            if (!PitchNames.TryParseRoot(chord.Root, out var rootPitchClass))
            {
                rootPitchClass = 0;
            }

            var voicingIntervals = ChordPadBuilder.VoicingFor(chord.Quality);
            var startSample = (int)(chord.StartSec * SampleRate);
            var chordDuration = chord.EndSec - chord.StartSec;
            var numSamples = (int)(chordDuration * SampleRate);

            // Voicing across Bass (Octave 2/3) and Triad (Octave 3/4)
            var pianoMidiPitches = new List<int>
            {
                (2 + 1) * 12 + rootPitchClass, // Bass root (e.g. C3 = 48)
                (2 + 1) * 12 + ((rootPitchClass + 7) % 12), // Bass 5th (e.g. G3 = 55)
            };

            foreach (var iv in voicingIntervals)
            {
                pianoMidiPitches.Add((3 + 1) * 12 + ((rootPitchClass + iv) % 12)); // Octave 4
            }

            foreach (var midi in pianoMidiPitches)
            {
                var freq = MidiToFreq(midi);
                var pan = (midi % 12) / 12.0f; // Subtle stereo spread across voices
                var leftGain = 0.28f * (1.0f - pan * 0.4f);
                var rightGain = 0.28f * (0.6f + pan * 0.4f);

                for (var s = 0; s < numSamples; s++)
                {
                    var targetIdx = startSample + s;
                    if (targetIdx >= left.Length) break;

                    var t = s / (double)SampleRate;

                    // Piano harmonic decay model
                    var envelope = Math.Exp(-1.8 * t) * (1.0 - Math.Exp(-50.0 * t));
                    var sample = (float)(
                        0.60 * Math.Sin(2.0 * Math.PI * freq * t) +
                        0.25 * Math.Sin(2.0 * Math.PI * 2 * freq * t) * Math.Exp(-0.8 * t) +
                        0.10 * Math.Sin(2.0 * Math.PI * 3 * freq * t) * Math.Exp(-1.5 * t) +
                        0.05 * Math.Sin(2.0 * Math.PI * 4 * freq * t) * Math.Exp(-2.5 * t)
                    ) * (float)envelope;

                    left[targetIdx] += sample * leftGain;
                    right[targetIdx] += sample * rightGain;
                }
            }
        }
    }

    private static void RenderFluteMelody(
        IReadOnlyList<NoteEvent> notes,
        float[] left,
        float[] right)
    {
        foreach (var note in notes)
        {
            var freq = MidiToFreq(note.MidiPitch);
            var startSample = (int)(note.StartSec * SampleRate);
            var noteSampleCount = (int)((note.DurationSec + 0.08) * SampleRate);
            var velocityScale = (note.Velocity / 127.0f) * 0.48f;

            for (var s = 0; s < noteSampleCount; s++)
            {
                var targetIdx = startSample + s;
                if (targetIdx >= left.Length) break;

                var t = s / (double)SampleRate;
                var noteDur = note.DurationSec;

                // ADSR Flute Envelope
                double env;
                const double attack = 0.035;
                const double decay = 0.040;
                var sustain = 0.85;
                const double release = 0.065;

                if (t < attack)
                {
                    env = t / attack;
                }
                else if (t < attack + decay)
                {
                    env = 1.0 - (1.0 - sustain) * ((t - attack) / decay);
                }
                else if (t < noteDur)
                {
                    env = sustain;
                }
                else if (t < noteDur + release)
                {
                    env = sustain * (1.0 - (t - noteDur) / release);
                }
                else
                {
                    break;
                }

                // Flute additive synthesis (Rich in odd harmonics & warm fundamental, pure stable pitch with zero vibrato)
                var sample = (float)(
                    0.72 * Math.Sin(2.0 * Math.PI * freq * t) +
                    0.20 * Math.Sin(2.0 * Math.PI * 2 * freq * t) +
                    0.06 * Math.Sin(2.0 * Math.PI * 3 * freq * t) +
                    0.02 * Math.Sin(2.0 * Math.PI * 4 * freq * t)
                ) * (float)env * velocityScale;

                // Centered with warm stereo shimmer
                left[targetIdx] += sample * 0.95f;
                right[targetIdx] += sample * 0.95f;
            }
        }
    }

    private static void MasterAudio(float[] left, float[] right)
    {
        // Peak scan
        var maxPeak = 0.001f;
        for (var i = 0; i < left.Length; i++)
        {
            var l = Math.Abs(left[i]);
            var r = Math.Abs(right[i]);
            if (l > maxPeak) maxPeak = l;
            if (r > maxPeak) maxPeak = r;
        }

        // Target -0.5 dB peak (approx 0.94)
        var targetPeak = 0.94f;
        var gain = maxPeak > targetPeak ? targetPeak / maxPeak : 1.0f;

        for (var i = 0; i < left.Length; i++)
        {
            left[i] = Math.Clamp(left[i] * gain, -0.99f, 0.99f);
            right[i] = Math.Clamp(right[i] * gain, -0.99f, 0.99f);
        }
    }

    private static byte[] EncodePcmWav(float[] left, float[] right)
    {
        var sampleCount = left.Length;
        var byteCount = sampleCount * Channels * (BitsPerSample / 8);
        var wavBytes = new byte[44 + byteCount];
        var span = wavBytes.AsSpan();

        // 1. RIFF Header
        span[0] = (byte)'R'; span[1] = (byte)'I'; span[2] = (byte)'F'; span[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), 36 + byteCount);
        span[8] = (byte)'W'; span[9] = (byte)'A'; span[10] = (byte)'V'; span[11] = (byte)'E';

        // 2. fmt subchunk
        span[12] = (byte)'f'; span[13] = (byte)'m'; span[14] = (byte)'t'; span[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16); // Subchunk1Size for PCM
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), 1);  // AudioFormat 1 = PCM
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), SampleRate * Channels * (BitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), (short)(Channels * (BitsPerSample / 8)));
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), BitsPerSample);

        // 3. data subchunk
        span[36] = (byte)'d'; span[37] = (byte)'a'; span[38] = (byte)'t'; span[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), byteCount);

        // 4. Interleaved 16-bit PCM Samples
        var offset = 44;
        for (var i = 0; i < sampleCount; i++)
        {
            var sampleL = (short)(left[i] * 32767.0f);
            var sampleR = (short)(right[i] * 32767.0f);

            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset, 2), sampleL);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(offset + 2, 2), sampleR);
            offset += 4;
        }

        return wavBytes;
    }

    private static double MidiToFreq(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
}
