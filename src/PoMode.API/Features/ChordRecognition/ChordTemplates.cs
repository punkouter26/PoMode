using PoMode.API.Features.ModalAnalysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>A single chord label: its display symbol, root pitch-class name, triad quality, and root pitch class.</summary>
public sealed record ChordCandidate(string Symbol, string Root, string Quality, int RootPitchClass);

/// <summary>
/// The 24-triad chord vocabulary (12 major + 12 minor) plus "no chord", expressed as L2-normalised
/// 12-bin chroma templates. Every template is derived by rotating one base major and one base minor
/// shape to each of the 12 roots — never hand-written per key — so the set cannot drift out of sync.
/// </summary>
public static class ChordTemplates
{
    private static readonly int[] MajorIntervals = [0, 4, 7];
    private static readonly int[] MinorIntervals = [0, 3, 7];

    /// <summary>24 entries (12 major, 12 minor), each an L2-normalised 12-vector rotated from the base triad shape.</summary>
    public static IReadOnlyList<(ChordCandidate Chord, float[] Template)> All { get; } = Build();

    /// <summary>The candidate representing the absence of a recognisable chord.</summary>
    public static ChordCandidate NoChord { get; } = new("N", "N", "N", -1);

    private static IReadOnlyList<(ChordCandidate Chord, float[] Template)> Build()
    {
        var entries = new List<(ChordCandidate Chord, float[] Template)>();
        for (var root = 0; root < 12; root++)
        {
            entries.Add((MakeCandidate(root, "maj", suffix: string.Empty), MakeTemplate(root, MajorIntervals)));
        }
        for (var root = 0; root < 12; root++)
        {
            entries.Add((MakeCandidate(root, "min", suffix: "m"), MakeTemplate(root, MinorIntervals)));
        }
        return entries;
    }

    private static ChordCandidate MakeCandidate(int root, string quality, string suffix)
    {
        var rootName = PitchNames.Name(root);
        return new ChordCandidate(rootName + suffix, rootName, quality, root);
    }

    private static float[] MakeTemplate(int root, int[] intervals)
    {
        var template = new float[12];
        foreach (var interval in intervals)
        {
            template[(root + interval) % 12] = 1f;
        }

        var magnitude = (float)Math.Sqrt(template.Sum(v => v * v));
        for (var i = 0; i < template.Length; i++)
        {
            template[i] /= magnitude;
        }
        return template;
    }
}
