using System.Numerics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Deterministic modal derivation (spec §6). Pure function over Phase-2 artifacts — no audio, no models.</summary>
public static class ModalAnalysisEngine
{
    private const int MinimumDistinctPitchClasses = 3;
    private const int MaxMatchesPerWindow = 4;
    private const double CharacteristicBonus = 0.15;

    public static ModalResult Analyze(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<ChordSpan> chords,
        double tempoBpm = 120.0)
    {
        var tonic = TonicDetector.Detect(notes, chords);
        var secondsPerMeasure = 4.0 * 60.0 / (tempoBpm <= 0 ? 120.0 : tempoBpm);

        var windows = new List<ModalWindow>(chords.Count);
        for (var index = 0; index < chords.Count; index++)
        {
            var chord = chords[index];
            var intervals = notes
                .Where(note => note.StartSec >= chord.StartSec && note.StartSec < chord.EndSec)
                .Select(note => (((note.MidiPitch % 12) + 12) % 12 - tonic.PitchClass + 12) % 12)
                .Distinct()
                .Order()
                .ToArray();

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
            TempoBpm: tempoBpm,
            TempoEstimated: true,
            Windows: windows);
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
            var coverage = sungCount == 0 ? 0 : (double)matched.Length / sungCount;

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
