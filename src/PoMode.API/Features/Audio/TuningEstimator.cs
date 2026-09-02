using System.Numerics;
using PoMode.API.Features.ChordRecognition;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Audio;

/// <summary>
/// How far a recording sits from concert A=440, in cents, with a measure of how much the evidence
/// agreed. <see cref="Cents"/> is always in [-50, +50): half a semitone either way is the whole
/// space, because a full semitone is just a different note.
/// </summary>
/// <param name="Cents">Signed offset. Negative means the recording is flat of A=440.</param>
/// <param name="Confidence">
/// 0..1. The resultant length of the circular mean: 1 when every peak agrees on one offset, near 0
/// when the deviations are spread evenly. A well-tuned or a uniformly detuned recording both score
/// high; an ensemble out of tune with itself scores low, which is the signal that no single offset
/// describes it.
/// </param>
public sealed record TuningEstimate(double Cents, double Confidence)
{
    /// <summary>
    /// Below this the deviations did not cluster, so no single number describes the recording and
    /// the honest correction is none. Chosen from the synthesised fixtures: a detuned tone reads
    /// well above it, noise reads far below.
    /// </summary>
    public const double MinConfidence = 0.3;

    /// <summary>The offset worth acting on: zero unless the evidence actually clustered.</summary>
    public double AppliedCents => Confidence >= MinConfidence ? Cents : 0.0;

    public static TuningEstimate None => new(0.0, 0.0);
}

/// <summary>
/// Estimates a recording's global tuning offset from its spectrum.
///
/// <para>The pipeline anchors every pitch decision to A=440 through
/// <see cref="ScaleModes.MidiFromFrequency"/>. A recording that sits flat of that — tape or vinyl
/// transferred at the wrong speed, an orchestra at A=442, a historical A=435 — pushes every note
/// toward the boundary between two semitones. Notes near the edge then round to the wrong pitch
/// class, and the chroma extractor, which splits each bin between its two nearest semitones, leaks
/// energy into the neighbour on every frame. One number measured once fixes both.</para>
///
/// <para>This measures rather than changes: nothing here touches the audio. The offset is applied
/// when frequencies are interpreted, so there is no resampling and no artifacts.</para>
/// </summary>
public static class TuningEstimator
{
    /// <summary>Matches the chromagram's analysis rate, so both stages see the same spectrum.</summary>
    private const int TargetSampleRate = ChromaExtractor.TargetSampleRate;

    private const int WindowSize = 8192;
    private const int HopSize = 8192;

    /// <summary>
    /// Fundamentals and low harmonics live here. Below 100 Hz a bin is worth tens of cents even
    /// after interpolation; above 2 kHz the spectrum is high harmonics and percussive noise, which
    /// carry no reliable tuning information.
    /// </summary>
    private const double MinFrequencyHz = 100.0;
    private const double MaxFrequencyHz = 2000.0;

    /// <summary>A peak must reach this share of its frame's loudest bin to count.</summary>
    private const double PeakFloor = 0.1;

    public static TuningEstimate Estimate(AudioBuffer buffer)
    {
        var mono = AudioDecoder.ToMono(buffer);
        if (mono.SampleRate != TargetSampleRate)
        {
            mono = AudioDecoder.Resample(mono, TargetSampleRate);
        }

        var samples = mono.Samples;
        if (samples.Length < WindowSize)
        {
            return TuningEstimate.None;
        }

        // Deviation from the nearest semitone is circular with a period of one semitone: -49 cents
        // and +51 are the same place. Summing unit vectors rather than raw numbers is what makes the
        // wrap free, and the length of the sum falls out as the confidence.
        var sumSin = 0.0;
        var sumCos = 0.0;
        var sumWeight = 0.0;

        var window = HannWindow(WindowSize);
        var fft = new Complex[WindowSize];
        var magnitudes = new double[(WindowSize / 2) + 1];

        for (var start = 0; start + WindowSize <= samples.Length; start += HopSize)
        {
            for (var i = 0; i < WindowSize; i++)
            {
                fft[i] = new Complex(samples[start + i] * window[i], 0);
            }
            Fft.Transform(fft);

            var frameMax = 0.0;
            for (var bin = 0; bin < magnitudes.Length; bin++)
            {
                magnitudes[bin] = fft[bin].Magnitude;
                frameMax = Math.Max(frameMax, magnitudes[bin]);
            }
            if (frameMax <= 0)
            {
                continue;
            }

            var floor = frameMax * PeakFloor;
            var minBin = Math.Max(1, (int)(MinFrequencyHz * WindowSize / TargetSampleRate));
            var maxBin = Math.Min(magnitudes.Length - 2, (int)(MaxFrequencyHz * WindowSize / TargetSampleRate));

            for (var bin = minBin; bin <= maxBin; bin++)
            {
                var magnitude = magnitudes[bin];
                if (magnitude < floor || magnitude <= magnitudes[bin - 1] || magnitude < magnitudes[bin + 1])
                {
                    continue;
                }

                var refined = InterpolatePeak(magnitudes[bin - 1], magnitude, magnitudes[bin + 1]);
                var frequency = (bin + refined) * TargetSampleRate / (double)WindowSize;
                if (frequency <= 0)
                {
                    continue;
                }

                var midi = ScaleModes.MidiFromFrequency(frequency);
                var deviation = midi - Math.Round(midi);       // semitones, [-0.5, 0.5]
                var angle = deviation * 2.0 * Math.PI;          // one semitone = one full turn

                sumSin += magnitude * Math.Sin(angle);
                sumCos += magnitude * Math.Cos(angle);
                sumWeight += magnitude;
            }
        }

        if (sumWeight <= 0)
        {
            return TuningEstimate.None;
        }

        var confidence = Math.Sqrt((sumSin * sumSin) + (sumCos * sumCos)) / sumWeight;
        var cents = Math.Atan2(sumSin, sumCos) / (2.0 * Math.PI) * 100.0;
        return new TuningEstimate(cents, confidence);
    }

    /// <summary>
    /// Sub-bin peak position by fitting a parabola through the three log magnitudes. Without this the
    /// estimate is useless: at 22.05 kHz over 8192 samples a bin spans 2.7 Hz, which is 23 cents at
    /// 200 Hz — coarser than the offsets being measured.
    /// </summary>
    private static double InterpolatePeak(double left, double centre, double right)
    {
        if (left <= 0 || centre <= 0 || right <= 0)
        {
            return 0.0;
        }

        var a = Math.Log(left);
        var b = Math.Log(centre);
        var c = Math.Log(right);
        var denominator = a - (2 * b) + c;
        if (Math.Abs(denominator) < 1e-12)
        {
            return 0.0;
        }

        return Math.Clamp(0.5 * (a - c) / denominator, -0.5, 0.5);
    }

    private static double[] HannWindow(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++)
        {
            window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
        }
        return window;
    }
}
