using PoMode.API.Features.Audio;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// Free, model-less monophonic melody transcription: the classic YIN pitch estimator
/// (de Cheveigné &amp; Kawahara 2002) — cumulative-mean-normalised difference with the paper's dip
/// threshold, parabolic interpolation, a short median filter against octave blips, and run-length
/// note segmentation. Built for one voice at a time, which is exactly what the separated vocal
/// stem is. Pure DSP: no model download, deterministic, available everywhere (including Azure,
/// where the ONNX executors are disabled).
/// </summary>
public static class YinMelodyTranscriber
{
    private const int TargetSampleRate = 22050;

    /// <summary>~46 ms of signal per estimate — several periods of every pitch in range.</summary>
    private const int WindowSize = 1024;

    /// <summary>~23 ms hop → ~43 estimates per second.</summary>
    private const int HopSize = 512;

    /// <summary>D2 — below any melodic vocal; keeps the lag search bounded.</summary>
    private const double MinFrequencyHz = 73.4;

    /// <summary>C6 — above typical sung melodies.</summary>
    private const double MaxFrequencyHz = 1046.5;

    /// <summary>The YIN paper's recommended absolute dip threshold.</summary>
    private const double DipThreshold = 0.15;

    /// <summary>Frames whose best dip is still worse than this count as unvoiced (noise, breath).</summary>
    private const double VoicedCeiling = 0.35;

    /// <summary>Runs shorter than this are transcription flicker, not notes.</summary>
    private const double MinNoteSeconds = 0.09;

    /// <summary>Frames quieter than this RMS are silence regardless of what the lag search finds.</summary>
    private const double SilenceRms = 0.01;

    public static IReadOnlyList<NoteEvent> Transcribe(AudioBuffer buffer)
    {
        var mono = AudioDecoder.ToMono(buffer);
        if (mono.SampleRate != TargetSampleRate)
        {
            mono = AudioDecoder.Resample(mono, TargetSampleRate);
        }
        var samples = mono.Samples;

        var tauMin = Math.Max(2, (int)(TargetSampleRate / MaxFrequencyHz));
        var tauMax = (int)(TargetSampleRate / MinFrequencyHz);
        var frameSpan = WindowSize + tauMax;
        if (samples.Length < frameSpan)
        {
            return [];
        }

        var frameCount = ((samples.Length - frameSpan) / HopSize) + 1;
        var midis = new double?[frameCount];
        var rmsPerFrame = new double[frameCount];
        var cmndf = new double[tauMax + 1];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var start = frame * HopSize;
            rmsPerFrame[frame] = Rms(samples, start, WindowSize);
            if (rmsPerFrame[frame] < SilenceRms)
            {
                continue;
            }
            midis[frame] = EstimateMidi(samples, start, tauMin, tauMax, cmndf);
        }

        MedianSmooth(midis);
        return Segment(midis, rmsPerFrame);
    }

    private static double Rms(float[] samples, int start, int count)
    {
        var sum = 0.0;
        for (var i = start; i < start + count; i++)
        {
            sum += (double)samples[i] * samples[i];
        }
        return Math.Sqrt(sum / count);
    }

    /// <summary>One YIN estimate over samples[start .. start+WindowSize+tauMax), or null when unvoiced.</summary>
    private static double? EstimateMidi(
        float[] samples, int start, int tauMin, int tauMax, double[] cmndf)
    {
        // Difference function d(tau) and cumulative-mean normalisation d'(tau) fused with the dip
        // search: the normalisation is cumulative, so cmndf[tau] is final the moment tau is
        // computed and the scan can stop at the first accepted dip's local minimum instead of
        // always paying the full lag range. Voiced frames dip well before tauMax, so this is the
        // tracker's main cost saving; only unvoiced frames pay the whole range.
        var cumulative = 0.0;
        cmndf[0] = 1.0;
        var best = -1;
        var inDip = false;
        for (var tau = 1; tau <= tauMax; tau++)
        {
            var sum = 0.0;
            for (var j = start; j < start + WindowSize; j++)
            {
                var delta = (double)samples[j] - samples[j + tau];
                sum += delta * delta;
            }
            cumulative += sum;
            cmndf[tau] = cumulative > 0 ? sum * tau / cumulative : 1.0;

            if (tau < tauMin)
            {
                continue;
            }
            if (inDip && cmndf[tau] >= cmndf[tau - 1])
            {
                best = tau - 1;
                break;
            }
            inDip |= cmndf[tau] < DipThreshold;
        }
        if (inDip && best < 0)
        {
            best = tauMax; // the dip ran into the end of the range; its minimum is the last lag
        }
        if (best < 0)
        {
            // No dip anywhere: fall back to the global minimum, gated by the voicing ceiling.
            for (var tau = tauMin; tau <= tauMax; tau++)
            {
                if (best < 0 || cmndf[tau] < cmndf[best])
                {
                    best = tau;
                }
            }
        }
        if (best < 0 || cmndf[best] > VoicedCeiling)
        {
            return null;
        }

        // Parabolic interpolation around the winning lag recovers sub-sample precision.
        var refined = (double)best;
        if (best > tauMin && best < tauMax)
        {
            var left = cmndf[best - 1];
            var centre = cmndf[best];
            var right = cmndf[best + 1];
            var denominator = left - (2 * centre) + right;
            if (Math.Abs(denominator) > 1e-12)
            {
                refined = best + ((left - right) / (2 * denominator));
            }
        }

        return ScaleModes.MidiFromFrequency(TargetSampleRate / refined);
    }

    /// <summary>Replaces each voiced estimate with the median of its voiced ±2 neighbourhood —
    /// the cheap, standard defence against single-frame octave errors.</summary>
    private static void MedianSmooth(double?[] midis)
    {
        var smoothed = (double?[])midis.Clone();
        var window = new List<double>(5);
        for (var i = 0; i < midis.Length; i++)
        {
            if (midis[i] is null)
            {
                continue;
            }
            window.Clear();
            for (var j = Math.Max(0, i - 2); j <= Math.Min(midis.Length - 1, i + 2); j++)
            {
                if (midis[j] is { } value)
                {
                    window.Add(value);
                }
            }
            if (window.Count >= 3)
            {
                window.Sort();
                smoothed[i] = window[window.Count / 2];
            }
        }
        Array.Copy(smoothed, midis, midis.Length);
    }

    /// <summary>Merges consecutive frames on the same semitone into notes; velocity follows the
    /// run's loudness relative to the loudest voiced frame in the file.</summary>
    private static List<NoteEvent> Segment(double?[] midis, double[] rmsPerFrame)
    {
        var maxRms = 0.0;
        for (var i = 0; i < midis.Length; i++)
        {
            if (midis[i] is not null)
            {
                maxRms = Math.Max(maxRms, rmsPerFrame[i]);
            }
        }

        var notes = new List<NoteEvent>();
        var runStart = -1;
        var runPitch = 0;
        var runRmsSum = 0.0;
        for (var i = 0; i <= midis.Length; i++)
        {
            var pitch = i < midis.Length && midis[i] is { } midi
                ? (int?)Math.Clamp((int)Math.Round(midi), 0, 127)
                : null;

            if (runStart >= 0 && pitch != runPitch)
            {
                EmitRun(notes, runStart, i, runPitch, runRmsSum, maxRms);
                runStart = -1;
            }
            if (pitch is { } value && runStart < 0)
            {
                runStart = i;
                runPitch = value;
                runRmsSum = 0.0;
            }
            if (pitch is not null)
            {
                runRmsSum += rmsPerFrame[i];
            }
        }
        return notes;
    }

    private static void EmitRun(
        List<NoteEvent> notes, int startFrame, int endFrame, int pitch, double rmsSum, double maxRms)
    {
        var startSec = startFrame * HopSize / (double)TargetSampleRate;
        var durationSec = (endFrame - startFrame) * HopSize / (double)TargetSampleRate;
        if (durationSec < MinNoteSeconds)
        {
            return;
        }
        var meanRms = rmsSum / (endFrame - startFrame);
        var velocity = maxRms > 0
            ? Math.Clamp((int)Math.Round(127 * Math.Sqrt(meanRms / maxRms)), 20, 127)
            : 90;
        notes.Add(new NoteEvent(pitch, startSec, durationSec, velocity));
    }
}
