using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Practice;

/// <summary>
/// Scores one sung attempt against the phrase the server issued.
///
/// <para>Three components rather than one number, because they fail independently and call for
/// different practice. Pitch is the ear. Timing is the pulse. Purity is whether the singer stayed
/// inside the mode at all — and it is reported over <em>every</em> note heard, not just the matched
/// ones, because a phrase can hit every target and still be littered with notes from the parent key
/// in between, which is exactly the habit this feature is trying to break.</para>
///
/// <para>Nothing is reported in cents. The browser's note collector rounds pitch to the nearest
/// semitone before anything is posted, so an intonation figure derived from it would be arithmetic
/// performed on a rounding — the same reason the song statistics omit figures the beat grid cannot
/// support.</para>
/// </summary>
public static class ModeExerciseGrader
{
    /// <summary>Share of the beat an onset may miss by and still be the same note. Beyond it the
    /// singer is on a different beat, not a late one.</summary>
    private const double MatchWindowBeats = 0.5;

    /// <summary>Floor and ceiling on that window, so a very fast or very slow tempo still grades
    /// against a humanly sensible tolerance rather than a purely proportional one.</summary>
    private const double MinMatchWindowSec = 0.15;
    private const double MaxMatchWindowSec = 0.6;

    /// <summary>Timing is scored against this share of the beat: dead on the click is 100, a miss of
    /// a full tolerance is 0.</summary>
    private const double TimingToleranceBeats = 0.35;

    /// <summary>Wrong octave, right note: half credit. The ear found the pitch class, the voice could
    /// not reach the register, and telling a singer those are the same mistake is unhelpful.</summary>
    private const double OctaveCredit = 0.5;

    private const double PitchWeight = 0.50;
    private const double TimingWeight = 0.25;
    private const double PurityWeight = 0.25;

    /// <summary>Most outside notes worth naming back. A list of every stray pitch is a transcript,
    /// not advice.</summary>
    private const int MaxOutsideNotes = 4;

    public static ExerciseScoreDto Grade(ModeExerciseDto exercise, IReadOnlyList<NoteEvent> sungNotes)
    {
        var targets = exercise.TargetNotes;
        var sung = sungNotes.OrderBy(note => note.StartSec).ToList();
        var secondsPerBeat = 60.0 / exercise.Bpm;
        var matchWindow = Math.Clamp(
            secondsPerBeat * MatchWindowBeats, MinMatchWindowSec, MaxMatchWindowSec);

        var matched = MatchNotes(targets, sung, matchWindow);
        var grades = new List<NoteGradeDto>(targets.Count);
        var offsets = new List<double>();
        var credit = 0.0;
        var hits = 0;

        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var label = NoteLabel(target.MidiPitch);
            if (matched[index] is not { } sungNote)
            {
                grades.Add(new NoteGradeDto(
                    index, target.MidiPitch, label, target.StartSec,
                    NoteVerdict.Missed, null, null, null));
                continue;
            }

            var verdict = sungNote.MidiPitch == target.MidiPitch ? NoteVerdict.Hit
                : SamePitchClass(sungNote.MidiPitch, target.MidiPitch) ? NoteVerdict.Octave
                : NoteVerdict.Wrong;

            credit += verdict switch
            {
                NoteVerdict.Hit => 1.0,
                NoteVerdict.Octave => OctaveCredit,
                _ => 0.0,
            };
            if (verdict == NoteVerdict.Hit)
            {
                hits++;
            }

            var offset = sungNote.StartSec - target.StartSec;
            // Only notes the singer actually found contribute a timing figure. A wrong note sung
            // exactly on the click is a pitch failure, and letting it prop up the timing score would
            // make a confidently wrong attempt read as half right.
            if (verdict != NoteVerdict.Wrong)
            {
                offsets.Add(offset);
            }

            grades.Add(new NoteGradeDto(
                index, target.MidiPitch, label, target.StartSec,
                verdict, sungNote.MidiPitch, NoteLabel(sungNote.MidiPitch), offset));
        }

        var pitchPercent = targets.Count == 0 ? 0 : Percent(credit / targets.Count);
        var medianOffset = Median(offsets);
        var timingPercent = offsets.Count == 0
            ? 0
            : Percent(1.0 - (Math.Abs(medianOffset) / (secondsPerBeat * TimingToleranceBeats)));

        var intervals = ScaleModes.Intervals(exercise.Mode);
        var inside = sung.Count(note => intervals.Contains(
            PitchNames.IntervalAboveTonic(note.MidiPitch, exercise.TonicPitchClass)));
        var purityPercent = sung.Count == 0 ? 0 : Percent(inside / (double)sung.Count);

        var characteristics = ScaleModes.CharacteristicIntervals(exercise.Mode);
        var characteristicSung = sung.Any(note => characteristics.Contains(
            PitchNames.IntervalAboveTonic(note.MidiPitch, exercise.TonicPitchClass)));

        var outside = OutsideNotes(sung, exercise.TonicPitchClass, intervals);
        var extras = sung.Count - matched.Count(match => match is not null);
        var score = sung.Count == 0
            ? 0
            : (int)Math.Round(
                (pitchPercent * PitchWeight) + (timingPercent * TimingWeight) + (purityPercent * PurityWeight),
                MidpointRounding.AwayFromZero);

        return new ExerciseScoreDto(
            Score: score,
            PitchPercent: pitchPercent,
            TimingPercent: timingPercent,
            ModePurityPercent: purityPercent,
            CharacteristicSung: characteristicSung,
            NotesHit: hits,
            NotesTotal: targets.Count,
            ExtraNotes: Math.Max(extras, 0),
            MedianTimingOffsetSec: Math.Round(medianOffset, 3),
            Rushing: offsets.Count > 0 && medianOffset < 0,
            OutsideNotes: outside,
            Notes: grades,
            Verdict: Verdict(exercise, score, sung.Count, pitchPercent, characteristicSung),
            Advice: Advice(exercise, sung.Count, pitchPercent, timingPercent, purityPercent,
                medianOffset, offsets.Count, characteristicSung, outside));
    }

    /// <summary>
    /// Pairs each target with the nearest unused sung note inside the window, targets in time order.
    ///
    /// <para>Greedy rather than globally optimal, and deliberately so: an exercise is one note per
    /// beat sung front to back, so the nearest-onset pairing is the one a listener would make. A
    /// global alignment would happily match a singer's last note to the first target to save two
    /// misses, and report a phrase sung backwards as nearly correct.</para>
    /// </summary>
    private static NoteEvent?[] MatchNotes(
        IReadOnlyList<NoteEvent> targets, IReadOnlyList<NoteEvent> sung, double window)
    {
        var matched = new NoteEvent?[targets.Count];
        var used = new bool[sung.Count];

        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var bestDistance = window;
            var best = -1;

            for (var candidate = 0; candidate < sung.Count; candidate++)
            {
                if (used[candidate])
                {
                    continue;
                }
                var distance = Math.Abs(sung[candidate].StartSec - target.StartSec);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            if (best >= 0)
            {
                used[best] = true;
                matched[index] = sung[best];
            }
        }

        return matched;
    }

    /// <summary>The note names sung that the mode does not contain, commonest first.</summary>
    private static List<string> OutsideNotes(
        IReadOnlyList<NoteEvent> sung, int tonicPitchClass, IReadOnlyList<int> intervals)
        => [.. sung
            .Select(note => PitchNames.IntervalAboveTonic(note.MidiPitch, tonicPitchClass))
            .Where(interval => !intervals.Contains(interval))
            .GroupBy(interval => interval)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(MaxOutsideNotes)
            .Select(group => PitchNames.Name(tonicPitchClass + group.Key))];

    /// <summary>
    /// One sentence on how the attempt went. Follows the fingerprint's rule — a figure too weak to
    /// stand is left out rather than hedged — so an attempt with nothing in it says so plainly
    /// instead of reporting a score of zero as if zero had been measured.
    /// </summary>
    private static string Verdict(
        ModeExerciseDto exercise, int score, int sungCount, int pitchPercent, bool characteristicSung)
    {
        if (sungCount == 0)
        {
            return "Nothing was heard. Check the microphone, then sing along with the clicks.";
        }

        var mode = $"{exercise.TonicName} {exercise.Mode}";
        return score switch
        {
            >= 90 => $"That is {mode}. Pitch, pulse and note set all line up.",
            >= 75 when characteristicSung =>
                $"Solidly {mode} — you sang the degree that names it, and most of the phrase landed.",
            >= 75 => $"Close to {mode}. The notes are right; the degree that names the mode never arrived.",
            >= 55 when pitchPercent >= 70 =>
                $"The shape of {mode} is there, but it drifts.",
            >= 55 => $"Some of {mode} came through. Enough to build on, not enough to call it the mode yet.",
            >= 30 => $"That is not {mode} yet. Play the phrase again and sing it back one note at a time.",
            _ => $"Little of this matches {mode}. Try the slowest tempo and the scale run first.",
        };
    }

    /// <summary>
    /// The single most useful thing to fix, or null when there is nothing worth saying. One piece of
    /// advice, not a list: an attempt that scored badly on everything needs the worst thing named,
    /// and three simultaneous corrections are how a singer fixes none of them.
    /// </summary>
    private static string? Advice(
        ModeExerciseDto exercise,
        int sungCount,
        int pitchPercent,
        int timingPercent,
        int purityPercent,
        double medianOffset,
        int timedNotes,
        bool characteristicSung,
        IReadOnlyList<string> outside)
    {
        if (sungCount == 0)
        {
            return null;
        }

        var degrees = string.Join(" and the ", exercise.CharacteristicDegrees);

        // Ordered by how much each failure costs the exercise. Singing outside the mode is worst:
        // it means the note set itself has not landed, and nothing else can be practised until it has.
        if (purityPercent < 60 && outside.Count > 0)
        {
            return $"{outside.Count} note{(outside.Count == 1 ? "" : "s")} outside the mode kept turning up "
                + $"({string.Join(", ", outside)}). Sing the scale run first until only the mode's own "
                + "notes come out.";
        }

        if (!characteristicSung && exercise.CharacteristicDegrees.Count > 0)
        {
            return $"The {degrees} never arrived. That degree is the whole difference between this mode "
                + "and its neighbours — try the leap exercise until it sits under your voice.";
        }

        if (pitchPercent < 70)
        {
            return "Play the phrase through once more before singing. Matching a note you have just "
                + "heard is a different skill from finding it cold, and it is the one to build first.";
        }

        if (timingPercent < 60 && timedNotes > 0)
        {
            return medianOffset < 0
                ? $"You are ahead of the click by about {Math.Abs(medianOffset):0.00}s. Let the beat "
                    + "arrive before you move."
                : $"You are behind the click by about {medianOffset:0.00}s. Breathe a beat earlier so "
                    + "the note starts on the pulse rather than after it.";
        }

        return null;
    }

    private static bool SamePitchClass(int left, int right)
        => ((left % 12) + 12) % 12 == ((right % 12) + 12) % 12;

    /// <summary>Sharp-spelled name plus octave, e.g. "D4" — the spelling every other surface uses.</summary>
    private static string NoteLabel(int midiPitch)
        => $"{PitchNames.Name(midiPitch)}{(midiPitch / 12) - 1}";

    private static int Percent(double fraction)
        => (int)Math.Round(Math.Clamp(fraction, 0.0, 1.0) * 100.0, MidpointRounding.AwayFromZero);

    /// <summary>Median, not mean: one note sung a beat late would otherwise drag the whole figure.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}
