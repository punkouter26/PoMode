namespace PoMode.API.Features.ChordRecognition;

/// <summary>Matches a single chroma frame against the 24-triad template vocabulary.</summary>
public static class ChordMatcher
{
    private static readonly (ChordCandidate Chord, float[] Template)[] OrderedTemplates = [.. ChordTemplates.All
        .OrderBy(e => e.Chord.RootPitchClass)
        .ThenBy(e => e.Chord.Quality == "maj" ? 0 : 1)];

    /// <summary>
    /// Finds the chord template with the highest cosine similarity to <paramref name="chroma"/>.
    /// Ties break deterministically by ascending root pitch class, major before minor.
    /// Returns <see cref="ChordTemplates.NoChord"/> with score 0 when the input is all-zero or the
    /// best score falls below <paramref name="noChordThreshold"/>.
    /// </summary>
    public static (ChordCandidate Chord, double Score) Match(float[] chroma, double noChordThreshold = 0.55)
    {
        var magnitude = Math.Sqrt(chroma.Sum(v => (double)v * v));
        if (magnitude == 0)
        {
            return (ChordTemplates.NoChord, 0.0);
        }

        var best = ChordTemplates.NoChord;
        var bestScore = double.NegativeInfinity;

        foreach (var (chord, template) in OrderedTemplates)
        {
            var dot = 0.0;
            for (var i = 0; i < chroma.Length; i++)
            {
                dot += chroma[i] * template[i];
            }

            var score = dot / magnitude;
            if (score > bestScore)
            {
                bestScore = score;
                best = chord;
            }
        }

        return bestScore >= noChordThreshold ? (best, bestScore) : (ChordTemplates.NoChord, 0.0);
    }
}
