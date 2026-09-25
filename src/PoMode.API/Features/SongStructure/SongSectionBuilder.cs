using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStructure;

/// <summary>
/// Splits a song into structural sections — the verse/chorus shape, lettered A, B, A, C — from the
/// chord track alone, and gives each section the mode its modal windows agree on. Pure function over
/// stored artifacts: no audio is decoded, which is what lets it be derived on every request instead of
/// persisted.
///
/// <para>The method is the textbook one, kept small: one harmonic feature per bar, a self-similarity
/// matrix over the bars, Foote's checkerboard kernel slid down its diagonal to get a novelty curve,
/// peaks of that curve (at least <see cref="MinSectionBars"/> apart) as boundaries, and a greedy
/// clustering of the resulting segments into letters. Bars come from the tempo map when there is one,
/// from the beat grid otherwise, and from fixed four-beat windows at the analysed tempo as a last
/// resort — the same measure length the modal engine numbers its windows with.</para>
///
/// <para>A song that comes out as one section returns no sections at all: a ribbon reading "A" from
/// end to end says nothing, and the rule everywhere else in the app is that an empty answer beats a
/// vacuous one.</para>
/// </summary>
public static class SongSectionBuilder
{
    /// <summary>Half the checkerboard's width. Four bars a side compares one phrase with the next.</summary>
    private const int KernelHalfBars = 4;

    /// <summary>No section is shorter than a four-bar phrase; anything shorter is a fill, not a section.</summary>
    private const int MinSectionBars = 4;

    /// <summary>Normalised novelty below this is harmony moving within a section, not a new one.</summary>
    private const double MinNovelty = 0.1;

    /// <summary>A peak must also reach this share of the song's tallest peak to count.</summary>
    private const double RelativePeak = 0.35;

    /// <summary>Two segments share a letter from this cosine similarity of their chord content.</summary>
    private const double SameLetterSimilarity = 0.9;

    /// <summary>A section's mode is named when its leading mode takes this share of the window votes…</summary>
    private const double MinModeShare = 0.6;

    /// <summary>…and usable windows cover at least this share of the section.</summary>
    private const double MinModeCoverage = 0.3;

    /// <summary>Beyond this the bar grid is degenerate (a tempo read of 900 BPM); give no sections.</summary>
    private const int MaxBars = 1024;

    /// <summary>The root counts double so C major and A minor — two shared notes — stay distinct.</summary>
    private const double RootWeight = 2.0;

    private const string UnnamedModeToken = "--pm-fg-muted";

    /// <summary>The full width of the checkerboard in bars, for anything drawing it.</summary>
    public const int KernelBars = KernelHalfBars * 2;

    /// <summary>
    /// What the sections were cut from: the bar edges, the bar-by-bar similarity matrix, the novelty
    /// curve and the bars chosen as boundaries. <see cref="Sections"/> is empty when the harmony did
    /// not divide, which is a finding about the song; the measurements are still real.
    /// </summary>
    public sealed record Analysis(
        double[] BarEdges,
        double[,] Similarity,
        double[] Novelty,
        IReadOnlyList<int> Boundaries,
        IReadOnlyList<VisualSection> Sections);

    public static IReadOnlyList<VisualSection> Build(
        IReadOnlyList<ChordSpan> chords,
        ModalResult result,
        TempoMapDto? tempoMap = null,
        BeatGridDto? beats = null)
        => Analyse(chords, result, tempoMap, beats)?.Sections ?? [];

    /// <summary>The measurements behind <see cref="Build"/>, or null when there is no usable bar grid.</summary>
    public static Analysis? Analyse(
        IReadOnlyList<ChordSpan> chords,
        ModalResult result,
        TempoMapDto? tempoMap = null,
        BeatGridDto? beats = null)
    {
        var duration = chords.Count == 0 ? 0.0 : chords.Max(chord => chord.EndSec);
        if (duration <= 0)
        {
            return null;
        }

        var bars = BarStarts(duration, result, tempoMap, beats);
        var barCount = bars.Length - 1;
        if (barCount < MinSectionBars * 2 || barCount > MaxBars)
        {
            return null;
        }

        var features = BarFeatures(chords, bars);
        var similarity = SelfSimilarity(features);
        var novelty = Novelty(similarity);
        var boundaries = PickBoundaries(novelty, barCount);
        var sections = boundaries.Count == 0
            ? []
            : Sections(chords, result, bars, duration, Letter(features, boundaries, barCount));
        return new Analysis(bars, similarity, novelty, boundaries, sections);
    }

    private static IReadOnlyList<VisualSection> Sections(
        IReadOnlyList<ChordSpan> chords,
        ModalResult result,
        double[] bars,
        double duration,
        List<(string Letter, int FromBar, int ToBar)> segments)
    {
        if (segments.Count < 2)
        {
            return [];
        }

        var sections = new List<VisualSection>(segments.Count);
        foreach (var (letter, fromBar, toBar) in segments)
        {
            var start = bars[fromBar];
            var end = Math.Min(bars[toBar], duration);
            var mode = DominantMode(result.Windows, start, end);
            sections.Add(new VisualSection(
                Index: sections.Count,
                Letter: letter,
                StartSec: start,
                EndSec: end,
                Mode: mode?.ToString(),
                ColourToken: ColourToken(mode),
                Label: Caption(letter, mode, chords, result.TonicPitchClass, start, end)));
        }
        return sections;
    }

    // ---- Bar grid ---------------------------------------------------------------------------

    /// <summary>Bar edges from 0 to <paramref name="duration"/>: bar <c>i</c> is <c>[edges[i], edges[i+1])</c>.</summary>
    private static double[] BarStarts(double duration, ModalResult result, TempoMapDto? tempoMap, BeatGridDto? beats)
    {
        var starts = new List<double>();
        double barSec;

        if (tempoMap is { Measures.Count: > 1 })
        {
            // The measured downbeats, with the song's median bar length carrying the grid past the
            // last one — the map stops where its confidence did, the chords may not.
            starts.AddRange(tempoMap.Measures.Select(measure => measure.StartSec).Distinct().Order());
            var lengths = starts.Zip(starts.Skip(1), (a, b) => b - a).Where(length => length > 0).Order().ToArray();
            barSec = lengths.Length == 0 ? 0 : lengths[lengths.Length / 2];
        }
        else if (beats is { Bpm: > 0, Confidence: > 0 })
        {
            barSec = 4 * 60.0 / beats.Bpm;
            // Phase the grid to the first beat, then walk it back to the start of the song.
            starts.Add(beats.FirstBeatSec - (Math.Floor(beats.FirstBeatSec / barSec) * barSec));
        }
        else
        {
            barSec = 4 * 60.0 / (result.TempoBpm > 0 ? result.TempoBpm : 120.0);
            starts.Add(0);
        }

        if (barSec <= 0.25)
        {
            return [0, duration];
        }
        if (starts[0] > 0.01)
        {
            // A pickup before the first downbeat is its own short bar rather than lost time.
            starts.Insert(0, 0);
        }
        while (starts[^1] < duration && starts.Count <= MaxBars + 1)
        {
            starts.Add(starts[^1] + barSec);
        }

        // The last bar ends with the song, however far into it the song stops.
        var edges = starts.Where(start => start < duration).ToList();
        edges.Add(duration);
        return [.. edges];
    }

    // ---- Features, similarity, novelty ------------------------------------------------------

    /// <summary>
    /// A 12-bin chroma per bar from the chords sounding in it, weighted by how long each sounds and
    /// with the root counted double. L2-normalised; an all-zero vector means "no chord in this bar".
    /// </summary>
    private static double[][] BarFeatures(IReadOnlyList<ChordSpan> chords, double[] bars)
    {
        var barCount = bars.Length - 1;
        var features = new double[barCount][];
        for (var bar = 0; bar < barCount; bar++)
        {
            features[bar] = new double[12];
        }

        foreach (var chord in chords)
        {
            if (!PitchNames.TryParseRoot(chord.Root, out var root))
            {
                continue;
            }
            var voicing = ChordPadBuilder.VoicingFor(chord.Quality);
            // Bars are sorted, so only the bars this chord overlaps are visited.
            var found = Array.BinarySearch(bars, chord.StartSec);
            var first = Math.Max(found >= 0 ? found : ~found - 1, 0);
            for (var bar = first; bar < barCount && bars[bar] < chord.EndSec; bar++)
            {
                var overlap = Math.Min(chord.EndSec, bars[bar + 1]) - Math.Max(chord.StartSec, bars[bar]);
                if (overlap <= 0)
                {
                    continue;
                }
                foreach (var interval in voicing)
                {
                    features[bar][(root + interval) % 12] += overlap * (interval == 0 ? RootWeight : 1.0);
                }
            }
        }

        foreach (var feature in features)
        {
            Normalise(feature);
        }
        return features;
    }

    /// <summary>
    /// Cosine similarity between every pair of bars. Two chordless bars count as identical rather
    /// than unrelated — a stretch with no harmony is one coherent section, not a run of boundaries.
    /// </summary>
    private static double[,] SelfSimilarity(double[][] features)
    {
        var count = features.Length;
        var empty = features.Select(feature => feature.All(value => value == 0)).ToArray();
        var similarity = new double[count, count];
        for (var i = 0; i < count; i++)
        {
            for (var j = i; j < count; j++)
            {
                var value = empty[i] || empty[j] ? (empty[i] && empty[j] ? 1.0 : 0.0) : Dot(features[i], features[j]);
                similarity[i, j] = value;
                similarity[j, i] = value;
            }
        }
        return similarity;
    }

    /// <summary>
    /// Foote novelty at each bar line: a Gaussian-tapered checkerboard centred between bar
    /// <c>i-1</c> and bar <c>i</c>, positive where the blocks before and after are each self-similar
    /// and unlike each other. Normalised by the kernel's mass, so a clean cut between two unrelated,
    /// internally uniform blocks scores 0.5 and a uniform stretch scores about 0.
    /// </summary>
    private static double[] Novelty(double[,] similarity)
    {
        var count = similarity.GetLength(0);
        var novelty = new double[count];
        for (var i = 1; i < count; i++)
        {
            double sum = 0, mass = 0;
            for (var a = -KernelHalfBars; a < KernelHalfBars; a++)
            {
                var row = i + a;
                if (row < 0 || row >= count)
                {
                    continue;
                }
                for (var b = -KernelHalfBars; b < KernelHalfBars; b++)
                {
                    var column = i + b;
                    if (column < 0 || column >= count)
                    {
                        continue;
                    }
                    var weight = Taper(a) * Taper(b);
                    sum += ((a < 0) == (b < 0) ? weight : -weight) * similarity[row, column];
                    mass += weight;
                }
            }
            novelty[i] = mass > 0 ? sum / mass : 0;
        }
        return novelty;
    }

    /// <summary>Symmetric about the bar line (offset −½), so bars either side weigh the same.</summary>
    private static double Taper(int offset)
    {
        var distance = (offset + 0.5) / (KernelHalfBars / 2.0);
        return Math.Exp(-0.5 * distance * distance);
    }

    /// <summary>
    /// Local maxima of the novelty curve, tallest first, each kept only if it leaves at least
    /// <see cref="MinSectionBars"/> to the song's ends and to every boundary already kept.
    /// Candidates start a full kernel in from either end, where the checkerboard is whole.
    /// </summary>
    private static List<int> PickBoundaries(double[] novelty, int barCount)
    {
        var from = Math.Max(MinSectionBars, KernelHalfBars);
        var to = barCount - Math.Max(MinSectionBars, KernelHalfBars);
        var peaks = new List<int>();
        for (var i = from; i <= to; i++)
        {
            if (novelty[i] >= MinNovelty && novelty[i] >= novelty[i - 1] && novelty[i] > novelty[i + 1])
            {
                peaks.Add(i);
            }
        }
        if (peaks.Count == 0)
        {
            return [];
        }

        var tallest = peaks.Max(peak => novelty[peak]);
        var kept = new List<int>();
        foreach (var peak in peaks.Where(peak => novelty[peak] >= RelativePeak * tallest).OrderByDescending(peak => novelty[peak]))
        {
            if (kept.All(boundary => Math.Abs(boundary - peak) >= MinSectionBars))
            {
                kept.Add(peak);
            }
        }
        kept.Sort();
        return kept;
    }

    // ---- Lettering --------------------------------------------------------------------------

    /// <summary>
    /// Letters the segments between boundaries: each takes the letter of the first earlier segment
    /// whose overall chord content it matches, or the next unused letter. Adjacent segments that
    /// end up with the same letter are merged — a boundary between A and A is a peak the harmony
    /// did not bear out.
    /// </summary>
    private static List<(string Letter, int FromBar, int ToBar)> Letter(
        double[][] features, List<int> boundaries, int barCount)
    {
        var edges = new List<int> { 0 };
        edges.AddRange(boundaries);
        edges.Add(barCount);

        var prototypes = new List<double[]>();
        var segments = new List<(string Letter, int FromBar, int ToBar)>();
        for (var index = 0; index + 1 < edges.Count; index++)
        {
            var from = edges[index];
            var to = edges[index + 1];
            var profile = new double[12];
            for (var bar = from; bar < to; bar++)
            {
                for (var pitch = 0; pitch < 12; pitch++)
                {
                    profile[pitch] += features[bar][pitch];
                }
            }
            Normalise(profile);

            var match = prototypes.FindIndex(prototype => Dot(prototype, profile) >= SameLetterSimilarity);
            if (match < 0)
            {
                prototypes.Add(profile);
                match = prototypes.Count - 1;
            }
            var letter = ((char)('A' + Math.Min(match, 25))).ToString();

            if (segments.Count > 0 && segments[^1].Letter == letter)
            {
                segments[^1] = (letter, segments[^1].FromBar, to);
            }
            else
            {
                segments.Add((letter, from, to));
            }
        }
        return segments;
    }

    // ---- Mode and caption -------------------------------------------------------------------

    /// <summary>
    /// The mode the section's windows vote for, weighted by overlap and confidence — or null when
    /// the vote is split or too little of the section had enough sung material to vote at all.
    /// </summary>
    private static ScaleMode? DominantMode(IReadOnlyList<ModalWindow> windows, double start, double end)
    {
        var length = end - start;
        if (length <= 0)
        {
            return null;
        }

        var votes = new Dictionary<ScaleMode, double>();
        var covered = 0.0;
        foreach (var window in windows)
        {
            if (window.InsufficientEvidence || window.Matches.Count == 0)
            {
                continue;
            }
            var overlap = Math.Min(window.EndSec, end) - Math.Max(window.StartSec, start);
            if (overlap <= 0)
            {
                continue;
            }
            covered += overlap;
            var top = window.Matches[0];
            votes[top.Mode] = votes.GetValueOrDefault(top.Mode) + (overlap * top.Confidence);
        }

        var total = votes.Values.Sum();
        if (total <= 0 || covered / length < MinModeCoverage)
        {
            return null;
        }
        var leader = votes.OrderByDescending(vote => vote.Value).ThenBy(vote => (int)vote.Key).First();
        return leader.Value / total >= MinModeShare ? leader.Key : null;
    }

    /// <summary>
    /// "Section B", then the mode when one was named, then the mode's own chord — Mixolydian's ♭VII,
    /// Dorian's IV — but only when that chord really sounds in the section, because naming a chord
    /// the section never plays would be decoration posing as evidence.
    /// </summary>
    private static string Caption(
        string letter, ScaleMode? mode, IReadOnlyList<ChordSpan> chords, int tonicPitchClass, double start, double end)
    {
        var caption = $"Section {letter}";
        if (mode is not { } named)
        {
            return caption;
        }

        caption += $" · {named}";
        if (ModeEvidenceBuilder.CharacteristicChord(named) is { } signature)
        {
            var rootPitchClass = (tonicPitchClass + signature.RootInterval) % 12;
            var sounded = chords.Any(chord =>
                chord.StartSec < end && chord.EndSec > start
                && ChordPadBuilder.VoicingFor(chord.Quality) is [0, 4, 7]
                && PitchNames.TryParseRoot(chord.Root, out var root) && root == rootPitchClass);
            if (sounded)
            {
                caption += $" ({signature.Numeral} chord)";
            }
        }
        return caption;
    }

    /// <summary>The seven modes have theme tokens; the pentatonics and an unnamed mode share a neutral one.</summary>
    private static string ColourToken(ScaleMode? mode) => mode switch
    {
        ScaleMode.Ionian or ScaleMode.Dorian or ScaleMode.Phrygian or ScaleMode.Lydian
            or ScaleMode.Mixolydian or ScaleMode.Aeolian or ScaleMode.Locrian
            => $"--pm-mode-{mode.Value.ToString().ToLowerInvariant()}",
        _ => UnnamedModeToken,
    };

    private static double Dot(double[] left, double[] right)
    {
        var sum = 0.0;
        for (var i = 0; i < left.Length; i++)
        {
            sum += left[i] * right[i];
        }
        return sum;
    }

    private static void Normalise(double[] vector)
    {
        var magnitude = Math.Sqrt(Dot(vector, vector));
        if (magnitude <= 0)
        {
            return;
        }
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= magnitude;
        }
    }
}
