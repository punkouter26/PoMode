namespace PoMode.API.Features.ChordRecognition;

/// <summary>
/// Viterbi decoding over the 24-triad + no-chord state space: instead of labelling every chroma
/// frame independently (as <see cref="ChordMatcher"/> does), it finds the single most likely chord
/// *sequence*, where staying on the current chord is much more likely than switching. That prior
/// suppresses one-frame flicker at the source rather than median-filtering it away afterwards.
/// Free and deterministic — the same templates, plus dynamic programming.
/// </summary>
public static class ChordViterbiDecoder
{
    /// <summary>
    /// Probability of staying on the same chord frame-to-frame. At the chromagram's ~10.8 frames/s
    /// this expects chords lasting on the order of two seconds — a plausible pop harmonic rhythm.
    /// </summary>
    private const double SelfTransitionProbability = 0.95;

    /// <summary>Floor before taking logs, so an all-zero frame cannot produce -infinity.</summary>
    private const double EmissionFloor = 1e-3;

    public static IReadOnlyList<(ChordCandidate Chord, double Score)> Decode(IReadOnlyList<float[]> frames)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var templates = ChordTemplates.All;
        var stateCount = templates.Count + 1; // last state = no chord
        var noChordState = templates.Count;

        // Emissions: ChordMatcher's own cosine scoring, one row per frame. The no-chord state
        // emits the shared threshold, so silence and noise decode to "N" exactly as they would
        // in the per-frame matcher.
        var emissions = new double[frames.Count][];
        for (var t = 0; t < frames.Count; t++)
        {
            var chroma = frames[t];
            var magnitude = ChordMatcher.Magnitude(chroma);
            var row = new double[stateCount];
            if (magnitude > 0)
            {
                for (var s = 0; s < templates.Count; s++)
                {
                    row[s] = ChordMatcher.CosineScore(chroma, magnitude, templates[s].Template);
                }
            }
            row[noChordState] = ChordMatcher.NoChordThreshold;
            emissions[t] = row;
        }

        var logSelf = Math.Log(SelfTransitionProbability);
        var logSwitch = Math.Log((1 - SelfTransitionProbability) / (stateCount - 1));

        var scores = new double[stateCount];
        var previous = new double[stateCount];
        var backPointers = new int[frames.Count][];
        for (var s = 0; s < stateCount; s++)
        {
            scores[s] = Math.Log(Math.Max(emissions[0][s], EmissionFloor));
        }

        for (var t = 1; t < frames.Count; t++)
        {
            (previous, scores) = (scores, previous);
            // With a uniform switch cost, the best predecessor is either "stay" or the globally
            // best previous state — no inner state×state loop needed.
            var bestPrevious = 0;
            for (var s = 1; s < stateCount; s++)
            {
                if (previous[s] > previous[bestPrevious])
                {
                    bestPrevious = s;
                }
            }
            var pointers = new int[stateCount];
            for (var s = 0; s < stateCount; s++)
            {
                var stay = previous[s] + logSelf;
                var switchIn = previous[bestPrevious] + logSwitch;
                var fromSwitch = bestPrevious != s && switchIn > stay;
                pointers[s] = fromSwitch ? bestPrevious : s;
                scores[s] = (fromSwitch ? switchIn : stay)
                    + Math.Log(Math.Max(emissions[t][s], EmissionFloor));
            }
            backPointers[t] = pointers;
        }

        var state = 0;
        for (var s = 1; s < stateCount; s++)
        {
            if (scores[s] > scores[state])
            {
                state = s;
            }
        }

        var path = new (ChordCandidate Chord, double Score)[frames.Count];
        for (var t = frames.Count - 1; t >= 0; t--)
        {
            path[t] = state == noChordState
                ? (ChordTemplates.NoChord, 0.0)
                : (templates[state].Chord, emissions[t][state]);
            if (t > 0)
            {
                state = backPointers[t][state];
            }
        }
        return path;
    }
}
