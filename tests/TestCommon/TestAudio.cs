namespace PoMode.TestCommon;

/// <summary>Generates minimal valid audio fixtures for tests. Linked (not referenced) into each test project.</summary>
public static class TestAudio
{
    public static byte[] MakeWav(double seconds = 0.1, int sampleRate = 8000)
    {
        var samples = (int)(seconds * sampleRate);
        var dataSize = samples * 2; // PCM16 mono
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);          // PCM
        writer.Write((short)1);          // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);    // byte rate
        writer.Write((short)2);          // block align
        writer.Write((short)16);         // bits per sample
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]); // silence
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] MakeId3Mp3Header() => [.. "ID3"u8, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    public static byte[] MakeFrameSyncMp3Header() => [0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>A mono sine tone as a valid PCM16 WAV — real signal for decode and tempo tests.</summary>
    public static byte[] MakeTone(double seconds, double frequencyHz, int sampleRate = 22050, double amplitude = 0.5)
    {
        var count = (int)(seconds * sampleRate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = count * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var i = 0; i < count; i++)
        {
            var value = amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate);
            writer.Write((short)(value * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>A two-tone stereo mix (different frequency per channel) — synthetic input for stem separation tests.</summary>
    public static byte[] MakeTwoToneStereo(double seconds, double frequencyHzLeft, double frequencyHzRight, int sampleRate = 44100, double amplitude = 0.3)
    {
        var count = (int)(seconds * sampleRate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = count * 4; // stereo PCM16 = 4 bytes/frame
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2); // stereo
        writer.Write(sampleRate);
        writer.Write(sampleRate * 4); // byte rate
        writer.Write((short)4);       // block align
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var i = 0; i < count; i++)
        {
            var left = amplitude * Math.Sin(2 * Math.PI * frequencyHzLeft * i / sampleRate);
            var right = amplitude * Math.Sin(2 * Math.PI * frequencyHzRight * i / sampleRate);
            writer.Write((short)(left * short.MaxValue));
            writer.Write((short)(right * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Clicks at a fixed BPM — ground truth for the tempo estimator.</summary>
    public static byte[] MakeClickTrack(double seconds, double bpm, int sampleRate = 22050)
    {
        var count = (int)(seconds * sampleRate);
        var samples = new short[count];
        var samplesPerBeat = 60.0 / bpm * sampleRate;
        for (var beat = 0; beat * samplesPerBeat < count; beat++)
        {
            var start = (int)(beat * samplesPerBeat);
            for (var i = 0; i < 200 && start + i < count; i++)
            {
                // Short decaying burst = a sharp onset the envelope can find.
                var envelope = 1.0 - (i / 200.0);
                samples[start + i] = (short)(envelope * 0.8 * short.MaxValue * Math.Sin(2 * Math.PI * 1000 * i / sampleRate));
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = count * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Sums sine tones (each with a quieter octave partial) into a PCM16 WAV — synthetic "chord" audio.</summary>
    public static byte[] MakeChord(double seconds, int[] midiPitches, int sampleRate = 22050)
    {
        var count = (int)(seconds * sampleRate);
        var samples = new double[count];
        foreach (var midi in midiPitches)
        {
            var frequency = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
            for (var i = 0; i < count; i++)
            {
                var t = i / (double)sampleRate;
                samples[i] += Math.Sin(2 * Math.PI * frequency * t)
                    + (0.35 * Math.Sin(2 * Math.PI * frequency * 2 * t));
            }
        }

        return ToPcm16Wav(samples, sampleRate);
    }

    /// <summary>
    /// Sums arbitrarily placed sine tones into a PCM16 WAV — a synthetic "song" whose melody and
    /// pad ground truth the caller controls exactly. Unlike <see cref="MakeChord"/>, tones here
    /// have their own start, duration and level, so a lead line can ride over a chord bed.
    /// </summary>
    public static byte[] MakeSong(
        double seconds,
        IReadOnlyList<(int Midi, double StartSec, double DurationSec, double Amplitude)> tones,
        int sampleRate = 22050)
    {
        var count = (int)(seconds * sampleRate);
        var samples = new double[count];
        foreach (var (midi, startSec, durationSec, amplitude) in tones)
        {
            var frequency = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
            var start = (int)(startSec * sampleRate);
            var end = Math.Min(count, start + (int)(durationSec * sampleRate));
            for (var i = Math.Max(0, start); i < end; i++)
            {
                var t = (i - start) / (double)sampleRate;
                samples[i] += amplitude * Math.Sin(2 * Math.PI * frequency * t);
            }
        }
        return ToPcm16Wav(samples, sampleRate);
    }

    /// <summary>
    /// A voice-like line rather than a sine: a harmonic-rich source (partials falling off at 1/k),
    /// 5.5 Hz vibrato of ±<paramref name="vibratoCents"/>, a soft attack and release, and optional
    /// quiet noise standing in for the bleed a real separation leaves behind. What a vocal pitch
    /// model is for, and what a plain sine never exercises.
    /// </summary>
    public static byte[] MakeSungLine(
        double seconds,
        IReadOnlyList<(int Midi, double StartSec, double DurationSec)> notes,
        double vibratoCents = 40,
        double noiseLevel = 0.0,
        int sampleRate = 44100)
    {
        var count = (int)(seconds * sampleRate);
        var samples = new double[count];
        var random = new Random(7);
        foreach (var (midi, startSec, durationSec) in notes)
        {
            var frequency = 440.0 * Math.Pow(2, (midi - 69) / 12.0);
            var start = (int)(startSec * sampleRate);
            var length = (int)(durationSec * sampleRate);
            var phase = 0.0;
            for (var i = 0; i < length && start + i < count; i++)
            {
                var t = i / (double)sampleRate;
                var envelope = Math.Min(1.0, Math.Min(t / 0.04, (durationSec - t) / 0.06));
                var bent = frequency * Math.Pow(2, vibratoCents * Math.Sin(2 * Math.PI * 5.5 * t) / 1200.0);
                phase += 2 * Math.PI * bent / sampleRate;
                var value = 0.0;
                for (var k = 1; k <= 8 && bent * k < sampleRate / 2.0; k++)
                {
                    value += Math.Sin(k * phase) / k;
                }
                samples[start + i] += 0.5 * Math.Max(0, envelope) * value;
            }
        }
        if (noiseLevel > 0)
        {
            for (var i = 0; i < count; i++)
            {
                samples[i] += noiseLevel * ((random.NextDouble() * 2) - 1);
            }
        }
        return ToPcm16Wav(samples, sampleRate);
    }

    /// <summary>
    /// A 4/4 groove with a known beat and bar grid, for beat trackers: kick on 1 and 3, snare on 2 and
    /// 4, closed hi-hat eighths, and a bass note that changes on every bar's downbeat (the cue a
    /// listener uses to hear where bar one is). Silence before <paramref name="leadInSec"/>, so bar
    /// one does not coincide with t=0. Returns the true beat and downbeat times with the audio.
    /// </summary>
    public static (byte[] Wav, double[] Beats, double[] Downbeats) MakeDrumLoop(
        int bars, double bpm, double leadInSec, int sampleRate = 44100)
    {
        var beatSec = 60.0 / bpm;
        var seconds = leadInSec + (bars * 4 * beatSec) + 1.0;
        var count = (int)(seconds * sampleRate);
        var samples = new double[count];
        var random = new Random(11);
        int[] bassLine = [36, 41, 43, 38]; // C2 F2 G2 D2, one per bar

        void Add(double atSec, double durationSec, Func<double, double> voice)
        {
            var start = (int)(atSec * sampleRate);
            for (var i = 0; i < (int)(durationSec * sampleRate) && start + i < count; i++)
            {
                samples[start + i] += voice(i / (double)sampleRate);
            }
        }

        var beats = new List<double>();
        var downbeats = new List<double>();
        for (var bar = 0; bar < bars; bar++)
        {
            var barStart = leadInSec + (bar * 4 * beatSec);
            downbeats.Add(barStart);
            var bass = 440.0 * Math.Pow(2, (bassLine[bar % bassLine.Length] - 69) / 12.0);
            Add(barStart, (4 * beatSec) - 0.05, t => 0.35 * Math.Sin(2 * Math.PI * bass * t) * Math.Exp(-t * 0.8));
            for (var beat = 0; beat < 4; beat++)
            {
                var at = barStart + (beat * beatSec);
                beats.Add(at);
                if (beat % 2 == 0)
                {
                    // Kick: a pitch-dropping sine thump.
                    Add(at, 0.18, t => 0.9 * Math.Sin(2 * Math.PI * (50 + (100 * Math.Exp(-t * 30))) * t) * Math.Exp(-t * 18));
                }
                else
                {
                    // Snare: a noise burst over a short tone.
                    Add(at, 0.15, t => ((0.5 * ((random.NextDouble() * 2) - 1)) + (0.3 * Math.Sin(2 * Math.PI * 190 * t))) * Math.Exp(-t * 25));
                }
                for (var half = 0; half < 2; half++)
                {
                    Add(at + (half * beatSec / 2), 0.04, t => 0.15 * ((random.NextDouble() * 2) - 1) * Math.Exp(-t * 90));
                }
            }
        }
        return (ToPcm16Wav(samples, sampleRate), [.. beats], [.. downbeats]);
    }

    private static byte[] ToPcm16Wav(double[] samples, int sampleRate)
    {
        var peak = samples.Length == 0 ? 1.0 : Math.Max(samples.Max(Math.Abs), 1e-9);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write((short)(sample / peak * 0.8 * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>MIDI pitches for a root-position triad in octave 3. Quality: "maj" or "min".</summary>
    public static int[] Triad(int rootPitchClass, string quality)
    {
        var root = 48 + rootPitchClass; // C3 = 48
        var third = quality == "min" ? root + 3 : root + 4;
        return [root, third, root + 7];
    }
}
