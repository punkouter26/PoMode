using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Derives every song/melody statistic, plus the plain-English fingerprint paragraph, from the
/// artifacts a finished job already stores. Pure function — no audio, no models, no I/O — so it is
/// unit-testable and cannot drift from the pipeline.
///
/// <para>It takes the <see cref="VisualizationPayload"/> rather than raw <see cref="NoteEvent"/>s on
/// purpose: that payload already carries each note's <see cref="NoteRole"/> and pitch label, decided
/// once by <c>VisualizationBuilder</c>. Recomputing scale membership here would be a second, drifting
/// copy of the same music theory.</para>
///
/// <para>The chord spans come in raw because <see cref="VisualChord"/> keeps only the display symbol,
/// and the chord-tone stat needs the parsed root and quality.</para>
/// </summary>
public static class SongStatsBuilder
{
    private const int SchemaVersion = 1;

    /// <summary>A rest longer than this ends a phrase. Roughly a breath at any singable tempo.</summary>
    public const double PhraseGapSec = 0.5;

    /// <summary>1-2 semitones is a step; 3 or more is a leap. The conventional split.</summary>
    private const int LargestStep = 2;

    /// <summary>How many chords/moves the vocabulary stat lists. Enough to characterise, short enough to read.</summary>
    private const int TopChordCount = 5;
    private const int TopMoveCount = 3;

    public static SongStats Build(
        VisualizationPayload visual,
        IReadOnlyList<ChordSpan> chords,
        ModalResult result,
        BeatGridDto? beats)
    {
        // Every melody stat reads notes in time order; the artifact is already sorted, but a stat
        // that silently depends on that is a trap for whoever changes the pitch tracker next.
        var notes = visual.Notes.OrderBy(note => note.StartSec).ToArray();
        var beatSec = BeatPeriodSec(beats);

        var motion = BuildMotion(notes);
        var contour = BuildContour(notes);
        var tessitura = BuildTessitura(notes);
        var rhythm = BuildRhythm(notes, beats, beatSec);
        var phrases = BuildPhrases(notes);
        var harmonicRhythm = BuildHarmonicRhythm(chords, beatSec);
        var vocabulary = BuildChordVocabulary(chords);
        var chordTones = BuildChordTones(notes, chords);

        var inScalePercent = notes.Length == 0
            ? 0
            : Percent(notes.Count(note => note.Role != NoteRole.Outside), notes.Length);

        var stats = new SongStats(
            SchemaVersion: SchemaVersion,
            TonicName: result.TonicName,
            PrimaryMode: result.PrimaryMode?.ToString(),
            PrimaryConfidence: result.PrimaryConfidence,
            TempoBpm: result.TempoBpm,
            TempoEstimated: result.TempoEstimated,
            DurationSec: visual.DurationSec,
            MelodyNoteCount: notes.Length,
            NotesPerSecond: visual.DurationSec > 0 ? notes.Length / visual.DurationSec : 0,
            AverageNoteSec: notes.Length == 0 ? 0 : notes.Average(note => note.DurationSec),
            InScalePercent: inScalePercent,
            ModulationCount: ModulationCount(result),
            Motion: motion,
            BiggestLeap: BuildBiggestLeap(notes),
            Contour: contour,
            Tessitura: tessitura,
            Rhythm: rhythm,
            Phrases: phrases,
            HarmonicRhythm: harmonicRhythm,
            ChordVocabulary: vocabulary,
            ChordTones: chordTones,
            ModeVotes: BuildModeVotes(result),
            ScaleDegrees: BuildScaleDegrees(notes, result),
            Fingerprint: "");

        return stats with { Fingerprint = SongFingerprint.Write(stats) };
    }

    /// <summary>Seconds per beat, or null when the grid is missing or not trusted.</summary>
    private static double? BeatPeriodSec(BeatGridDto? beats)
        => beats is { Bpm: > 0, Confidence: > 0 } ? 60.0 / beats.Bpm : null;

    // ---- 1. Step vs leap -------------------------------------------------------------------

    private static MotionProfile BuildMotion(IReadOnlyList<VisualNote> notes)
    {
        if (notes.Count < 2)
        {
            return new MotionProfile(0, 0, 0, 0, 0, 0, 0, 0);
        }

        int repeats = 0, steps = 0, leaps = 0;
        var total = 0L;
        for (var i = 1; i < notes.Count; i++)
        {
            var distance = Math.Abs(notes[i].MidiPitch - notes[i - 1].MidiPitch);
            total += distance;
            if (distance == 0) repeats++;
            else if (distance <= LargestStep) steps++;
            else leaps++;
        }

        var count = notes.Count - 1;
        return new MotionProfile(
            IntervalCount: count,
            RepeatCount: repeats,
            StepCount: steps,
            LeapCount: leaps,
            RepeatPercent: Percent(repeats, count),
            StepPercent: Percent(steps, count),
            LeapPercent: Percent(leaps, count),
            AverageIntervalSemitones: (double)total / count);
    }

    // ---- 2. Biggest leap -------------------------------------------------------------------

    private static LeapHighlight? BuildBiggestLeap(IReadOnlyList<VisualNote> notes)
    {
        if (notes.Count < 2)
        {
            return null;
        }

        var bestIndex = 0;
        var best = 0;
        for (var i = 1; i < notes.Count; i++)
        {
            var distance = Math.Abs(notes[i].MidiPitch - notes[i - 1].MidiPitch);
            if (distance > best)
            {
                best = distance;
                bestIndex = i;
            }
        }

        if (best == 0)
        {
            return null;
        }

        var from = notes[bestIndex - 1];
        var to = notes[bestIndex];
        return new LeapHighlight(
            Semitones: best,
            Ascending: to.MidiPitch > from.MidiPitch,
            AtSec: to.StartSec,
            FromLabel: from.PitchLabel,
            ToLabel: to.PitchLabel);
    }

    // ---- 3. Contour ------------------------------------------------------------------------

    /// <summary>
    /// Direction counts plus a one-word shape. The shape compares the average pitch of the first and
    /// last thirds against the middle third, so it describes the arc of the whole line rather than
    /// the accident of its first and last note.
    /// </summary>
    private static ContourProfile BuildContour(IReadOnlyList<VisualNote> notes)
    {
        if (notes.Count < 2)
        {
            return new ContourProfile(0, 0, 0, 0, "Level");
        }

        int rising = 0, falling = 0;
        for (var i = 1; i < notes.Count; i++)
        {
            var delta = notes[i].MidiPitch - notes[i - 1].MidiPitch;
            if (delta > 0) rising++;
            else if (delta < 0) falling++;
        }

        var moves = rising + falling;
        var net = notes[^1].MidiPitch - notes[0].MidiPitch;

        var third = Math.Max(1, notes.Count / 3);
        var head = notes.Take(third).Average(note => note.MidiPitch);
        var belly = notes.Skip(third).Take(Math.Max(1, notes.Count - (2 * third))).Average(note => note.MidiPitch);
        var tail = notes.TakeLast(third).Average(note => note.MidiPitch);

        // One semitone of average movement is noise; two is a shape worth naming.
        const double Meaningful = 2.0;
        var shape = (tail - head) switch
        {
            >= Meaningful => "Rising",
            <= -Meaningful => "Falling",
            _ when belly - Math.Max(head, tail) >= Meaningful => "Arch",
            _ when Math.Min(head, tail) - belly >= Meaningful => "Valley",
            _ => "Level",
        };

        return new ContourProfile(rising, falling, Percent(rising, moves), net, shape);
    }

    // ---- 4. Tessitura ----------------------------------------------------------------------

    private static TessituraProfile? BuildTessitura(IReadOnlyList<VisualNote> notes)
    {
        if (notes.Count == 0)
        {
            return null;
        }

        // Weighted by nothing: one note, one vote. Duration weighting would let a single held
        // pedal tone define the tessitura, which is the opposite of what the stat is for.
        var pitches = notes.Select(note => note.MidiPitch).Order().ToArray();
        var low = Percentile(pitches, 0.10);
        var high = Percentile(pitches, 0.90);
        var median = Percentile(pitches, 0.50);

        return new TessituraProfile(
            MedianMidi: median,
            MedianLabel: PitchLabel(median),
            LowMidi: low,
            LowLabel: PitchLabel(low),
            HighMidi: high,
            HighLabel: PitchLabel(high),
            SpanSemitones: high - low);
    }

    /// <summary>Nearest-rank percentile over an already-sorted array.</summary>
    private static int Percentile(int[] sorted, double fraction)
    {
        var index = (int)Math.Round(fraction * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    // ---- 5/6. Syncopation and note values --------------------------------------------------

    /// <summary>
    /// Classifies every onset by how far it sits from the nearest beat. Nearer the beat than the
    /// midpoint between beats counts as on-beat; nearer the midpoint counts as syncopated. Notes
    /// that are neither (a third of a beat away, say) are counted in neither figure, so the two
    /// percentages describe what they claim and do not have to sum to 100.
    /// </summary>
    private static RhythmProfile BuildRhythm(
        IReadOnlyList<VisualNote> notes, BeatGridDto? beats, double? beatSec)
    {
        if (beats is null || beatSec is not { } period || notes.Count == 0)
        {
            return new RhythmProfile(BeatGridUsable: false, 0, 0, []);
        }

        // A tenth of a beat is inside any human performance's timing spread; wider and an eighth-note
        // run would start counting as "on the beat".
        const double Tolerance = 0.10;

        int onBeat = 0, syncopated = 0;
        var buckets = new Dictionary<string, int>();
        foreach (var note in notes)
        {
            var phase = Phase(note.StartSec - beats.FirstBeatSec, period);
            var toBeat = Math.Min(phase, 1 - phase);
            var toOffBeat = Math.Abs(phase - 0.5);
            if (toBeat <= Tolerance) onBeat++;
            else if (toOffBeat <= Tolerance) syncopated++;

            var label = NoteValueLabel(note.DurationSec / period);
            buckets[label] = buckets.GetValueOrDefault(label) + 1;
        }

        var values = NoteValueOrder
            .Where(buckets.ContainsKey)
            .Select(label => new NoteValueBucket(label, buckets[label], Percent(buckets[label], notes.Count)))
            .ToArray();

        return new RhythmProfile(
            BeatGridUsable: true,
            OnBeatPercent: Percent(onBeat, notes.Count),
            SyncopationPercent: Percent(syncopated, notes.Count),
            NoteValues: values);
    }

    /// <summary>Position within the beat as 0..1, correct for onsets before the first beat.</summary>
    private static double Phase(double offsetSec, double periodSec)
    {
        var phase = (offsetSec / periodSec) % 1.0;
        return phase < 0 ? phase + 1 : phase;
    }

    private static readonly string[] NoteValueOrder =
        ["Sixteenth", "Eighth", "Quarter", "Half", "Whole or longer"];

    /// <summary>
    /// Buckets a duration measured in beats. The boundaries sit between the nominal values (0.375
    /// between a sixteenth and an eighth, and so on) so ordinary performed rubato does not reclassify
    /// a note.
    /// </summary>
    private static string NoteValueLabel(double beats) => beats switch
    {
        < 0.375 => "Sixteenth",
        < 0.75 => "Eighth",
        < 1.5 => "Quarter",
        < 3.0 => "Half",
        _ => "Whole or longer",
    };

    // ---- 7. Phrases ------------------------------------------------------------------------

    private static PhraseProfile BuildPhrases(IReadOnlyList<VisualNote> notes)
    {
        if (notes.Count == 0)
        {
            return new PhraseProfile(0, 0, 0, 0, 0);
        }

        var lengths = new List<double>();
        var noteCounts = new List<int>();
        var starts = new List<double>();

        var phraseStart = notes[0].StartSec;
        var phraseNotes = 1;
        var phraseEnd = notes[0].StartSec + notes[0].DurationSec;

        for (var i = 1; i < notes.Count; i++)
        {
            if (notes[i].StartSec - phraseEnd > PhraseGapSec)
            {
                lengths.Add(phraseEnd - phraseStart);
                noteCounts.Add(phraseNotes);
                starts.Add(phraseStart);
                phraseStart = notes[i].StartSec;
                phraseNotes = 0;
            }
            phraseNotes++;
            phraseEnd = Math.Max(phraseEnd, notes[i].StartSec + notes[i].DurationSec);
        }
        lengths.Add(phraseEnd - phraseStart);
        noteCounts.Add(phraseNotes);
        starts.Add(phraseStart);

        var longest = lengths.IndexOf(lengths.Max());
        return new PhraseProfile(
            Count: lengths.Count,
            AverageSec: lengths.Average(),
            AverageNotes: noteCounts.Average(),
            LongestSec: lengths[longest],
            LongestStartSec: starts[longest]);
    }

    // ---- 8. Harmonic rhythm ----------------------------------------------------------------

    private static HarmonicRhythmProfile BuildHarmonicRhythm(
        IReadOnlyList<ChordSpan> chords, double? beatSec)
    {
        if (chords.Count == 0)
        {
            return new HarmonicRhythmProfile(beatSec is not null, 0, 0, 0);
        }

        var averageSec = chords.Average(chord => chord.EndSec - chord.StartSec);
        var span = chords[^1].EndSec - chords[0].StartSec;

        return new HarmonicRhythmProfile(
            BeatGridUsable: beatSec is not null,
            AverageChordSec: averageSec,
            AverageChordBeats: beatSec is { } period ? averageSec / period : 0,
            ChordsPerMinute: span > 0 ? chords.Count / span * 60.0 : 0);
    }

    // ---- 9. Chord vocabulary ---------------------------------------------------------------

    private static ChordVocabularyProfile BuildChordVocabulary(IReadOnlyList<ChordSpan> chords)
    {
        if (chords.Count == 0)
        {
            return new ChordVocabularyProfile(0, [], []);
        }

        var counts = chords
            .GroupBy(chord => chord.Symbol, StringComparer.Ordinal)
            .Select(group => new ChordCount(group.Key, group.Count(), Percent(group.Count(), chords.Count)))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Symbol, StringComparer.Ordinal)
            .ToArray();

        // Only changes count as a move: a chord held across two adjacent spans is the same harmony,
        // and counting "C -> C" would drown the real progression.
        var moves = new Dictionary<(string From, string To), int>();
        for (var i = 1; i < chords.Count; i++)
        {
            var from = chords[i - 1].Symbol;
            var to = chords[i].Symbol;
            if (!string.Equals(from, to, StringComparison.Ordinal))
            {
                moves[(from, to)] = moves.GetValueOrDefault((from, to)) + 1;
            }
        }

        var topMoves = moves
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key.From, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.To, StringComparer.Ordinal)
            .Take(TopMoveCount)
            .Select(entry => new ChordMove(entry.Key.From, entry.Key.To, entry.Value))
            .ToArray();

        return new ChordVocabularyProfile(counts.Length, [.. counts.Take(TopChordCount)], topMoves);
    }

    // ---- 10. Melody against the chord ------------------------------------------------------

    /// <summary>
    /// Which chord degree each melody note lands on. Distinct from <see cref="NoteRole.ChordTone"/>,
    /// which answers only yes/no: a line that sits on thirds sounds nothing like one that sits on roots.
    /// </summary>
    private static ChordToneProfile BuildChordTones(
        IReadOnlyList<VisualNote> notes, IReadOnlyList<ChordSpan> chords)
    {
        int root = 0, third = 0, fifth = 0, seventh = 0, tension = 0, classified = 0;

        foreach (var note in notes)
        {
            var chord = ChordCovering(chords, note.StartSec);
            if (chord is null || !PitchNames.TryParseRoot(chord.Root, out var rootClass))
            {
                continue;
            }

            classified++;
            var interval = ((((note.MidiPitch % 12) + 12) % 12) - rootClass + 12) % 12;
            switch (interval)
            {
                case 0: root++; break;
                case 3 or 4: third++; break;
                case 7: fifth++; break;
                case 10 or 11: seventh++; break;
                default: tension++; break;
            }
        }

        return new ChordToneProfile(
            ClassifiedNotes: classified,
            RootPercent: Percent(root, classified),
            ThirdPercent: Percent(third, classified),
            FifthPercent: Percent(fifth, classified),
            SeventhPercent: Percent(seventh, classified),
            TensionPercent: Percent(tension, classified),
            ConsonancePercent: Percent(root + third + fifth, classified));
    }

    private static ChordSpan? ChordCovering(IReadOnlyList<ChordSpan> chords, double timeSec)
        => TimelineSearch.IndexCovering(chords, timeSec, static c => c.StartSec, static c => c.EndSec)
            is { } index ? chords[index] : null;

    // ---- Charts: how the mode was decided, and what the singer actually sang ----------------

    /// <summary>
    /// Tallies the per-window mode decisions into a whole-song scoreboard.
    ///
    /// <para>This is the engine's reasoning shown as it happened. <c>ModalResult.PrimaryMode</c> is a
    /// single verdict; the windows are the votes behind it, and a song whose winner took 40% of
    /// windows is a genuinely different claim from one that took 95%. Windows with insufficient
    /// evidence are excluded from the denominator rather than counted against every mode, so the
    /// percentages describe decided windows only.</para>
    ///
    /// <para>Only modes that won at least one window appear: a list of nine rows, six of them zero,
    /// is a worse chart than a list of the three that were actually in contention.</para>
    /// </summary>
    private static List<ModeVote> BuildModeVotes(ModalResult result)
    {
        var wins = new Dictionary<ScaleMode, (int Count, double ConfidenceSum)>();
        var decided = 0;

        foreach (var window in result.Windows)
        {
            if (window is not { InsufficientEvidence: false, Matches.Count: > 0 })
            {
                continue;
            }

            decided++;
            var match = window.Matches[0];
            var current = wins.GetValueOrDefault(match.Mode);
            wins[match.Mode] = (current.Count + 1, current.ConfidenceSum + match.Confidence);
        }

        return [.. wins
            .Select(entry => new ModeVote(
                Mode: entry.Key.ToString(),
                WindowsWon: entry.Value.Count,
                WindowPercent: Percent(entry.Value.Count, decided),
                AverageConfidence: entry.Value.ConfidenceSum / entry.Value.Count,
                IsPrimary: entry.Key == result.PrimaryMode,
                CharacteristicDegrees: [.. ModeDefinitions.CharacteristicIntervals(entry.Key)
                    .Select(PitchNames.IntervalLabel)]))
            .OrderByDescending(vote => vote.WindowsWon)
            .ThenByDescending(vote => vote.AverageConfidence)
            .ThenBy(vote => vote.Mode, StringComparer.Ordinal)];
    }

    /// <summary>
    /// How many melody notes land on each of the twelve degrees, always all twelve in ascending
    /// order — a bar chart with gaps for the unsung degrees is exactly the point, because the gaps
    /// are what identify the scale.
    /// </summary>
    private static List<DegreeUsage> BuildScaleDegrees(IReadOnlyList<VisualNote> notes, ModalResult result)
    {
        var counts = new int[12];
        foreach (var note in notes)
        {
            counts[PitchNames.IntervalAboveTonic(note.MidiPitch, result.TonicPitchClass)]++;
        }

        var modeMask = result.PrimaryMode is { } mode ? ModeDefinitions.Mask(mode) : 0;
        IReadOnlyList<int> characteristic = result.PrimaryMode is { } primary
            ? ModeDefinitions.CharacteristicIntervals(primary)
            : [];

        var degrees = new List<DegreeUsage>(12);
        for (var interval = 0; interval < 12; interval++)
        {
            degrees.Add(new DegreeUsage(
                Interval: interval,
                DegreeLabel: PitchNames.IntervalLabel(interval),
                NoteName: PitchNames.Name(result.TonicPitchClass + interval),
                NoteCount: counts[interval],
                Percent: Percent(counts[interval], notes.Count),
                InPrimaryMode: (modeMask & (1 << interval)) != 0,
                IsCharacteristic: characteristic.Contains(interval)));
        }
        return degrees;
    }

    // ---- Shared helpers --------------------------------------------------------------------

    /// <summary>
    /// How many times the song's top-ranked mode changes between consecutive windows. Windows with
    /// insufficient evidence are skipped rather than treated as a change, so a momentary gap in the
    /// evidence does not read as two modulations.
    /// </summary>
    private static int ModulationCount(ModalResult result)
    {
        var changes = 0;
        ScaleMode? previous = null;
        foreach (var window in result.Windows)
        {
            if (window is { InsufficientEvidence: false, Matches.Count: > 0 })
            {
                var mode = window.Matches[0].Mode;
                if (previous is { } last && last != mode)
                {
                    changes++;
                }
                previous = mode;
            }
        }
        return changes;
    }

    /// <summary>MIDI 60 is C4, so the octave is <c>midi / 12 - 1</c> — same rule as the canvas labels.</summary>
    private static string PitchLabel(int midiPitch)
        => $"{PitchNames.Name(midiPitch)}{(midiPitch / 12) - 1}";

    private static double Percent(int part, int whole) => whole == 0 ? 0 : part * 100.0 / whole;
}
