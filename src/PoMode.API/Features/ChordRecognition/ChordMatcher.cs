namespace PoMode.API.Features.ChordRecognition;

/// <summary>Matches a single chroma frame against the 24-triad template vocabulary.</summary>
public static class ChordMatcher
{
    /// <summary>The floor a chord's cosine score must beat to count as a chord at all — shared
    /// with <see cref="ChordViterbiDecoder"/>'s no-chord emission so both decoders agree on it.</summary>
    public const double NoChordThreshold = 0.55;

    private static readonly (ChordCandidate Chord, float[] Template)[] OrderedTemplates = [.. ChordTemplates.All
        .OrderBy(e => e.Chord.RootPitchClass)
        .ThenBy(e => e.Chord.Quality == "maj" ? 0 : 1)];

    /// <summary>L2 norm of a chroma frame.</summary>
    public static double Magnitude(float[] chroma) => Math.Sqrt(chroma.Sum(v => (double)v * v));

    /// <summary>Cosine similarity of a chroma frame against one L2-normalised template — the one
    /// scoring formula, used per-frame here and per-state by the Viterbi decoder.</summary>
    public static double CosineScore(float[] chroma, double magnitude, float[] template)
    {
        var dot = 0.0;
        for (var i = 0; i < chroma.Length; i++)
        {
            dot += chroma[i] * template[i];
        }
        return dot / magnitude;
    }

    /// <summary>
    /// Finds the chord template with the highest cosine similarity to <paramref name="chroma"/>.
    /// Ties break deterministically by ascending root pitch class, major before minor.
    /// Returns <see cref="ChordTemplates.NoChord"/> with score 0 when the input is all-zero or the
    /// best score falls below <paramref name="noChordThreshold"/>.
    /// </summary>
    public static (ChordCandidate Chord, double Score) Match(float[] chroma, double noChordThreshold = NoChordThreshold)
    {
        var magnitude = Magnitude(chroma);
        if (magnitude == 0)
        {
            return (ChordTemplates.NoChord, 0.0);
        }

        var best = ChordTemplates.NoChord;
        var bestScore = double.NegativeInfinity;

        foreach (var (chord, template) in OrderedTemplates)
        {
            var score = CosineScore(chroma, magnitude, template);
            if (score > bestScore)
            {
                bestScore = score;
                best = chord;
            }
        }

        return bestScore >= noChordThreshold ? (best, bestScore) : (ChordTemplates.NoChord, 0.0);
    }
}
