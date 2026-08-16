using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

public sealed record TonicEstimate(int PitchClass, double Confidence);

/// <summary>Krumhansl-Schmuckler key finding. Only the ROOT is used downstream; the major/minor
/// verdict is discarded because the per-window engine decides modes.</summary>
public static class TonicDetector
{
    private static readonly double[] MajorProfile =
        [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];

    private static readonly double[] MinorProfile =
        [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    public static TonicEstimate Detect(IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords)
    {
        var histogram = new double[12];
        foreach (var note in notes)
        {
            histogram[((note.MidiPitch % 12) + 12) % 12] += Math.Max(note.DurationSec, 0);
        }
        foreach (var chord in chords)
        {
            if (TryParseRoot(chord.Root, out var pitchClass))
            {
                histogram[pitchClass] += Math.Max(chord.EndSec - chord.StartSec, 0) * 0.5;
            }
        }

        if (histogram.Sum() <= 0)
        {
            return new TonicEstimate(0, 0.0);
        }

        var best = double.NegativeInfinity;
        var second = double.NegativeInfinity;
        var bestPitchClass = 0;
        for (var root = 0; root < 12; root++)
        {
            foreach (var profile in new[] { MajorProfile, MinorProfile })
            {
                var score = Correlate(histogram, profile, root);
                if (score > best)
                {
                    second = best;
                    best = score;
                    bestPitchClass = root;
                }
                else if (score > second)
                {
                    second = score;
                }
            }
        }

        var confidence = best <= 0 ? 0.0 : Math.Clamp((best - second) / best, 0.0, 1.0);
        return new TonicEstimate(bestPitchClass, confidence);
    }

    public static bool TryParseRoot(string root, out int pitchClass)
    {
        pitchClass = 0;
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var baseClass = char.ToUpperInvariant(root[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => -1,
        };
        if (baseClass < 0)
        {
            return false;
        }

        foreach (var accidental in root.Skip(1))
        {
            baseClass += accidental switch { '#' => 1, 'b' => -1, _ => 0 };
        }

        pitchClass = ((baseClass % 12) + 12) % 12;
        return true;
    }

    private static double Correlate(double[] histogram, double[] profile, int rotation)
    {
        var meanHistogram = histogram.Average();
        var meanProfile = profile.Average();
        double covariance = 0, histogramVariance = 0, profileVariance = 0;
        for (var i = 0; i < 12; i++)
        {
            var h = histogram[i] - meanHistogram;
            var p = profile[((i - rotation) % 12 + 12) % 12] - meanProfile;
            covariance += h * p;
            histogramVariance += h * h;
            profileVariance += p * p;
        }
        var denominator = Math.Sqrt(histogramVariance * profileVariance);
        return denominator <= 0 ? 0 : covariance / denominator;
    }
}
