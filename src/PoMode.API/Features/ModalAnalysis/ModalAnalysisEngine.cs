using System.Numerics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Deterministic modal derivation (spec §6). Pure function over Phase-2 artifacts — no audio, no models.</summary>
public static class ModalAnalysisEngine
{
    private const int MinimumDistinctPitchClasses = 3;
    private const int MaxMatchesPerWindow = 4;
    // Must stay strictly below the smallest possible coverage step (1/12 ≈ 0.0833, the
    // step size when 12 distinct pitch classes are sung) so the bonus can only break ties
    // within a coverage level and can never invert the ordering between coverage levels.
    private const double CharacteristicBonus = 0.05;

    public static ModalResult Analyze(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<ChordSpan> chords,
        double tempoBpm = 120.0,
        bool tempoEstimated = true,
        double tuningOffsetCents = 0.0)
    {
        var bpm = tempoBpm <= 0 ? 120.0 : tempoBpm;
        var tonic = TonicDetector.Detect(notes, chords);
        var secondsPerMeasure = 4.0 * 60.0 / bpm;

        var sortedNotes = notes.OrderBy(note => note.StartSec).ToArray();

        var windows = new List<ModalWindow>(chords.Count);
        for (var index = 0; index < chords.Count; index++)
        {
            var chord = chords[index];
            var intervals = SungIntervals(sortedNotes, chord, tonic.PitchClass);

            var vocalMask = intervals.Aggregate(0, (mask, interval) => mask | (1 << interval));
            var insufficient = intervals.Length < MinimumDistinctPitchClasses;
            var matches = insufficient ? [] : ScoreModes(vocalMask, intervals);

            windows.Add(new ModalWindow(
                Index: index,
                StartSec: chord.StartSec,
                EndSec: chord.EndSec,
                ChordSymbol: chord.Symbol,
                MeasureNumber: (int)(chord.StartSec / secondsPerMeasure) + 1,
                VocalMask: vocalMask,
                SungIntervals: intervals,
                InsufficientEvidence: insufficient,
                Matches: matches));
        }

        var (primaryMode, primaryConfidence) = PickPrimary(windows);

        return new ModalResult(
            SchemaVersion: 1,
            TonicPitchClass: tonic.PitchClass,
            TonicName: PitchNames.Name(tonic.PitchClass),
            TonicConfidence: tonic.Confidence,
            PrimaryMode: primaryMode,
            PrimaryConfidence: primaryConfidence,
            TempoBpm: bpm,
            TempoEstimated: tempoEstimated,
            Windows: windows,
            TuningOffsetCents: tuningOffsetCents);
    }

    /// <summary>Distinct, ascending intervals above the tonic sung inside the chord's half-open
    /// span. Binary-searches the start-sorted notes so each window only touches its own notes.</summary>
    private static int[] SungIntervals(NoteEvent[] sortedNotes, ChordSpan chord, int tonicPitchClass)
    {
        var seen = new bool[12];
        for (var index = TimelineSearch.LowerBound(sortedNotes, chord.StartSec, static n => n.StartSec);
             index < sortedNotes.Length && sortedNotes[index].StartSec < chord.EndSec;
             index++)
        {
            seen[PitchNames.IntervalAboveTonic(sortedNotes[index].MidiPitch, tonicPitchClass)] = true;
        }

        var intervals = new List<int>(12);
        for (var interval = 0; interval < 12; interval++)
        {
            if (seen[interval])
            {
                intervals.Add(interval);
            }
        }
        return [.. intervals];
    }

    private static IReadOnlyList<ModalMatch> ScoreModes(int vocalMask, int[] intervals)
    {
        var sungCount = BitOperations.PopCount((uint)vocalMask);
        var scored = new List<(ModalMatch Match, int ModeSize)>();

        foreach (var mode in ModeDefinitions.All)
        {
            var modeMask = ModeDefinitions.Mask(mode);
            var matched = intervals.Where(i => (modeMask & (1 << i)) != 0).ToArray();
            var outside = intervals.Where(i => (modeMask & (1 << i)) == 0).ToArray();
            var coverage = (double)matched.Length / sungCount;

            var characteristic = ModeDefinitions.CharacteristicIntervals(mode);
            var present = characteristic.Count(i => (vocalMask & (1 << i)) != 0);
            var bonus = characteristic.Count == 0 ? 0 : CharacteristicBonus * present / characteristic.Count;

            var confidence = Math.Clamp(coverage + bonus, 0.0, coverage < 1.0 ? 0.99 : 1.0);
            scored.Add((new ModalMatch(mode, confidence, matched, outside), ModeDefinitions.Intervals(mode).Count));
        }

        return scored
            .OrderByDescending(entry => entry.Match.Confidence)
            .ThenBy(entry => entry.ModeSize)
            .ThenBy(entry => (int)entry.Match.Mode)
            .Take(MaxMatchesPerWindow)
            .Select(entry => entry.Match)
            .ToArray();
    }

    private static (ScaleMode? Mode, double Confidence) PickPrimary(List<ModalWindow> windows)
    {
        var usable = windows.Where(window => !window.InsufficientEvidence && window.Matches.Count > 0).ToArray();
        if (usable.Length == 0)
        {
            return (null, 0.0);
        }

        var totals = new Dictionary<ScaleMode, double>();
        var weights = new Dictionary<ScaleMode, double>();
        foreach (var window in usable)
        {
            var top = window.Matches[0];
            var weight = Math.Max(window.EndSec - window.StartSec, 0.0001);
            totals[top.Mode] = totals.GetValueOrDefault(top.Mode) + top.Confidence * weight;
            weights[top.Mode] = weights.GetValueOrDefault(top.Mode) + weight;
        }

        var winner = totals.OrderByDescending(pair => pair.Value).ThenBy(pair => (int)pair.Key).First();
        return (winner.Key, totals[winner.Key] / weights[winner.Key]);
    }
}
