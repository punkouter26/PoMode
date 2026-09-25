using System.Globalization;
using PoMode.API.Features.ChordRecognition;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>
/// Answers "why this mode?" for a finished analysis: which tone separates the primary mode from the
/// modes one note away from it, how much of the song actually sounds that tone, and which neighbour
/// the evidence therefore rules out. Pure function over the stored artifacts — no audio, no I/O.
///
/// <para>The neighbours are computed, not tabulated: two seven-note modes are neighbours when their
/// interval sets differ by exactly one note, which yields the brightness chain (Lydian – Ionian –
/// Mixolydian – Dorian – Aeolian – Phrygian – Locrian) without a second hand-written copy of it.
/// The headline contrast is the one <see cref="ScaleModes.CharacteristicIntervals"/> names first, so
/// this, the HUD's badges and the note colours all point at the same degree.</para>
///
/// <para>Same wording rule as <c>SongFingerprint</c>: a weak figure is omitted, never hedged. A tone
/// the song never sounds is stated as never sounded, because that is a fact about the song rather
/// than doubt about the measurement.</para>
/// </summary>
public static class ModeEvidenceBuilder
{
    /// <summary>The rival's tone must carry at most this share of the mode's tone to be "ruled out".</summary>
    private const double RuleOutRatio = 0.2;

    /// <summary>A share of the melody below this is not a figure worth quoting.</summary>
    private const double MinQuotedMelodyPercent = 1.0;

    /// <summary>The rival's tone becomes counter-evidence from this share of the melody…</summary>
    private const double CounterMelodyPercent = 3.0;

    /// <summary>…or from this many chords sounding it.</summary>
    private const int CounterChordCount = 2;

    /// <summary>A pentatonic line is only described as one when it keeps to its five notes this much.</summary>
    private const double PentatonicInsidePercent = 90.0;

    /// <summary>Chords count half as much as sung time, the same weighting the tonic detector uses.</summary>
    private const double ChordWeight = 0.5;

    private static readonly int[] MajorScale = [0, 2, 4, 5, 7, 9, 11];

    private static readonly string[] Numerals = ["I", "II", "III", "IV", "V", "VI", "VII"];

    /// <summary>One neighbour: the mode's <c>Tone</c> against the <c>RivalTone</c> the rival has in its place.</summary>
    public readonly record struct Contrast(int Tone, ScaleMode Rival, int RivalTone);

    private static readonly Dictionary<ScaleMode, Contrast[]> ContrastTable = BuildContrastTable();

    /// <summary>The mode's one-note neighbours, headline first. Empty for a pentatonic scale, which
    /// has no one-note neighbour among the modes this app names.</summary>
    public static IReadOnlyList<Contrast> ContrastsFor(ScaleMode mode) => ContrastTable[mode];

    /// <summary>
    /// The whole-song readout for the primary mode, or null when there is nothing honest to say —
    /// no mode was named, or a pentatonic line wandered too far outside its five notes to be
    /// described as keeping to them.
    /// </summary>
    public static ModeEvidence? Build(
        IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords, ModalResult result)
    {
        if (result.PrimaryMode is not { } mode)
        {
            return null;
        }

        var tonic = result.TonicPitchClass;
        var sung = new double[12];
        var totalSung = 0.0;
        foreach (var note in notes)
        {
            var duration = Math.Max(note.DurationSec, 0);
            sung[PitchNames.IntervalAboveTonic(note.MidiPitch, tonic)] += duration;
            totalSung += duration;
        }

        var chordSeconds = new double[12];
        var chordCounts = new int[12];
        foreach (var chord in chords)
        {
            foreach (var interval in ChordIntervals(chord, tonic))
            {
                chordSeconds[interval] += Math.Max(chord.EndSec - chord.StartSec, 0);
                chordCounts[interval]++;
            }
        }

        var contrasts = ContrastsFor(mode);
        if (contrasts.Count == 0)
        {
            return PentatonicEvidence(mode, tonic, sung, totalSung);
        }

        double Percent(int interval) => totalSung > 0 ? 100.0 * sung[interval] / totalSung : 0;
        double Weight(int interval) => sung[interval] + (ChordWeight * chordSeconds[interval]);

        var measured = contrasts.Select(contrast =>
        {
            var tone = Weight(contrast.Tone);
            var rival = Weight(contrast.RivalTone);
            return new ModeContrast(
                Interval: contrast.Tone,
                Degree: DegreeLabel(mode, contrast.Tone),
                ToneName: ScaleModes.NoteName(tonic + contrast.Tone),
                Rival: contrast.Rival.ToString(),
                RivalDegree: DegreeLabel(contrast.Rival, contrast.RivalTone),
                RivalToneName: ScaleModes.NoteName(tonic + contrast.RivalTone),
                MelodyPercent: Percent(contrast.Tone),
                ChordCount: chordCounts[contrast.Tone],
                RivalMelodyPercent: Percent(contrast.RivalTone),
                RivalChordCount: chordCounts[contrast.RivalTone],
                RulesOut: tone > 0 && rival <= RuleOutRatio * tone);
        }).ToArray();

        var reasons = new List<string>();
        var headline = measured[0];
        if (Weight(contrasts[0].Tone) > 0)
        {
            reasons.Add(HeadlineSentence(mode, headline, chords.Count));
            if (headline.RulesOut)
            {
                reasons.Add(RuleOutSentence(headline, Weight(contrasts[0].Tone), Weight(contrasts[0].RivalTone)));
            }
        }
        else
        {
            reasons.Add($"The song never sounds the {headline.ToneName} ({headline.Degree}) that separates "
                + $"{mode} from {headline.Rival}, so that call rests on the rest of the scale.");
        }

        foreach (var secondary in measured.Skip(1).Where(contrast => contrast.RulesOut))
        {
            reasons.Add($"The {secondary.ToneName} ({secondary.Degree}) rules out {secondary.Rival}, "
                + $"which would need {secondary.RivalToneName} ({secondary.RivalDegree}) instead.");
        }

        return new ModeEvidence(mode.ToString(), reasons, CounterSentence(measured), measured);
    }

    /// <summary>
    /// The intervals whose notes the canvas rings: exactly the tones the reasons cite — the headline
    /// tone, plus any secondary tone that rules its rival out. Ringing a tone the text never mentions
    /// would leave the reader looking for an explanation that is not there. Empty for a pentatonic
    /// readout, whose evidence is the notes the line leaves out, and there is nothing to ring.
    /// </summary>
    public static IReadOnlyList<int> CitedIntervals(ModeEvidence? evidence)
        => evidence is null
            ? []
            : [.. evidence.Contrasts.Where((contrast, index) => index == 0 || contrast.RulesOut)
                .Select(contrast => contrast.Interval)];

    /// <summary>
    /// One sentence for a single window, or null. Stated only when the window settles the headline
    /// question by itself — it sang the mode's tone and never the rival's. A window that sang both
    /// has no one-line answer, and a hedged one would be worse than none.
    /// </summary>
    public static string? ForWindow(ModalWindow window, int tonicPitchClass)
    {
        if (window.InsufficientEvidence || window.Matches.Count == 0)
        {
            return null;
        }

        var mode = window.Matches[0].Mode;
        if (ContrastsFor(mode) is not [var headline, ..])
        {
            return null;
        }

        var sangTone = (window.VocalMask & (1 << headline.Tone)) != 0;
        var sangRival = (window.VocalMask & (1 << headline.RivalTone)) != 0;
        return sangTone && !sangRival
            ? $"The {ScaleModes.NoteName(tonicPitchClass + headline.Tone)} ({DegreeLabel(mode, headline.Tone)}) "
                + $"sung here rules out {headline.Rival}."
            : null;
    }

    /// <summary>
    /// The major triad inside <paramref name="mode"/> that contains its headline tone — Dorian's IV,
    /// Mixolydian's ♭VII, Phrygian's ♭II — as its root's interval above the tonic and its roman
    /// numeral. It is the chord that makes a mode audible under a melody, which is why a section
    /// label names it. Null for a pentatonic scale.
    /// </summary>
    public static (int RootInterval, string Numeral)? CharacteristicChord(ScaleMode mode)
    {
        if (ContrastsFor(mode) is not [var headline, ..])
        {
            return null;
        }

        var scale = ScaleModes.Intervals(mode);
        for (var degree = 0; degree < scale.Count; degree++)
        {
            var root = scale[degree];
            var third = scale[(degree + 2) % scale.Count];
            var fifth = scale[(degree + 4) % scale.Count];
            var isMajor = Above(root, third) == 4 && Above(root, fifth) == 7;
            if (isMajor && (root == headline.Tone || third == headline.Tone || fifth == headline.Tone))
            {
                // A numeral carries a sign only when it differs from the major key's: "IV", not "♮IV".
                var offset = root - MajorScale[degree];
                return (root, (offset == 0 ? "" : Accidental(offset)) + Numerals[degree]);
            }
        }
        return null;
    }

    /// <summary>
    /// A degree spelled against the major scale: its number from its position in the mode, its sign
    /// from how far it sits from the major scale's degree of the same number. That is why Locrian's
    /// tritone reads ♭5 while Lydian's reads ♯4 — the same interval, a different degree.
    /// </summary>
    public static string DegreeLabel(ScaleMode mode, int interval)
    {
        var index = ScaleModes.Intervals(mode).ToList().IndexOf(interval);
        return index < 0 || index >= MajorScale.Length
            ? PitchNames.IntervalLabel(interval)
            : Accidental(interval - MajorScale[index]) + (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Every interval above the tonic sounded by the chord's voicing; none for "N".</summary>
    public static IEnumerable<int> ChordIntervals(ChordSpan chord, int tonicPitchClass)
    {
        if (!PitchNames.TryParseRoot(chord.Root, out var root))
        {
            return [];
        }
        return ChordPadBuilder.VoicingFor(chord.Quality)
            .Select(interval => ((root + interval - tonicPitchClass) % 12 + 12) % 12)
            .Distinct();
    }

    private static string HeadlineSentence(ScaleMode mode, ModeContrast headline, int chordTotal)
    {
        var clauses = new List<string>(2);
        if (headline.MelodyPercent >= MinQuotedMelodyPercent)
        {
            clauses.Add($"it carries {Round(headline.MelodyPercent)}% of the sung time");
        }
        if (headline.ChordCount > 0)
        {
            clauses.Add($"sounds in {headline.ChordCount} of {chordTotal} chords");
        }

        var measured = clauses.Count == 0 ? "" : $": {string.Join(" and ", clauses)}";
        return $"The {headline.ToneName} ({headline.Degree}) is what separates {mode} from {headline.Rival}{measured}.";
    }

    private static string RuleOutSentence(ModeContrast headline, double toneWeight, double rivalWeight)
    {
        if (rivalWeight <= 0)
        {
            return $"{headline.Rival} would need {headline.RivalToneName} ({headline.RivalDegree}) instead, "
                + $"and the song never sounds it — that rules {headline.Rival} out.";
        }

        // The floor keeps the ratio honest: "more than 5 to 1" is true of 5.9 to 1, "6 to 1" is not.
        var ratio = (int)Math.Floor(toneWeight / rivalWeight);
        return $"The {headline.ToneName} outweighs the {headline.RivalToneName} ({headline.RivalDegree}) "
            + $"{headline.Rival} would need by more than {ratio} to 1, which rules {headline.Rival} out.";
    }

    /// <summary>The loudest argument for a neighbour, if any argument is loud enough to state.</summary>
    private static string? CounterSentence(IReadOnlyList<ModeContrast> measured)
    {
        var strongest = measured
            .Where(contrast => !contrast.RulesOut
                && (contrast.RivalMelodyPercent >= CounterMelodyPercent || contrast.RivalChordCount >= CounterChordCount))
            .OrderByDescending(contrast => contrast.RivalMelodyPercent + contrast.RivalChordCount)
            .FirstOrDefault();
        if (strongest is null)
        {
            return null;
        }

        var clauses = new List<string>(2);
        if (strongest.RivalMelodyPercent >= MinQuotedMelodyPercent)
        {
            clauses.Add($"takes {Round(strongest.RivalMelodyPercent)}% of the sung time");
        }
        if (strongest.RivalChordCount > 0)
        {
            clauses.Add($"sounds in {strongest.RivalChordCount} chords");
        }
        return $"Against it: the {strongest.RivalToneName} ({strongest.RivalDegree}) that {strongest.Rival} "
            + $"would need {string.Join(" and ", clauses)}.";
    }

    /// <summary>
    /// A pentatonic scale has no one-note neighbour, so its evidence is what it leaves out. Stated
    /// only when the line really does keep to its five notes; otherwise the readout is omitted.
    /// </summary>
    private static ModeEvidence? PentatonicEvidence(ScaleMode mode, int tonic, double[] sung, double totalSung)
    {
        if (totalSung <= 0)
        {
            return null;
        }

        var inside = ScaleModes.Intervals(mode).Sum(interval => sung[interval]);
        var insidePercent = 100.0 * inside / totalSung;
        if (insidePercent < PentatonicInsidePercent)
        {
            return null;
        }

        var names = string.Join(" · ", ScaleModes.NoteNames(tonic, mode));
        var sentence = $"The melody keeps to the five notes {names} for {Round(insidePercent)}% of its sung time; "
            + "leaving the other two out is what separates it from the seven-note modes.";
        return new ModeEvidence(mode.ToString(), [sentence], null, []);
    }

    private static Dictionary<ScaleMode, Contrast[]> BuildContrastTable()
    {
        var diatonic = ScaleModes.All.Where(mode => ScaleModes.Intervals(mode).Count == MajorScale.Length).ToArray();
        var table = new Dictionary<ScaleMode, Contrast[]>();
        foreach (var mode in ScaleModes.All)
        {
            if (!diatonic.Contains(mode))
            {
                table[mode] = [];
                continue;
            }

            var own = ScaleModes.Intervals(mode);
            var characteristic = ScaleModes.CharacteristicIntervals(mode).ToList();
            table[mode] = [.. diatonic
                .Where(rival => rival != mode)
                .Select(rival => (Rival: rival, Theirs: ScaleModes.Intervals(rival)))
                .Select(pair => (pair.Rival, Mine: own.Except(pair.Theirs).ToArray(), Theirs: pair.Theirs.Except(own).ToArray()))
                .Where(pair => pair.Mine.Length == 1 && pair.Theirs.Length == 1)
                .Select(pair => new Contrast(pair.Mine[0], pair.Rival, pair.Theirs[0]))
                // Headline first: the degree the shared characteristic table names first.
                .OrderBy(contrast => characteristic.Contains(contrast.Tone) ? characteristic.IndexOf(contrast.Tone) : int.MaxValue)
                .ThenBy(contrast => (int)contrast.Rival)];
        }
        return table;
    }

    private static int Above(int from, int to) => ((to - from) % 12 + 12) % 12;

    private static string Accidental(int offset) => offset switch
    {
        < 0 => "♭",
        > 0 => "♯",
        _ => "♮",
    };

    private static string Round(double value)
        => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}
