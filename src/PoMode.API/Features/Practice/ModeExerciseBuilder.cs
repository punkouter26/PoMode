using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Practice;

/// <summary>
/// Builds the short phrase a practice attempt is graded against.
///
/// <para>Deterministic in the same way <c>ModalMelodyGenerator</c> is, and for the same reason a hum
/// take can carry a reference to its backing instead of a copy: the six values on
/// <see cref="ExerciseAttemptRequest"/> regenerate the identical phrase here, so an attempt is graded
/// against the notes the server actually issued rather than against whatever the browser claims it
/// was shown.</para>
///
/// <para>The pitch pool is the mode's own scale built on its own tonic — not the parent key's. Same
/// ruling as the Mode Lab melody pool, and it matters most for the pentatonics, where drawing from
/// the parent would sound the two notes the scale exists to omit.</para>
/// </summary>
public static class ModeExerciseBuilder
{
    /// <summary>One note per beat. Slow enough to sing, and it makes the timing grade mean something:
    /// at a quarter-note pulse a late onset is a late onset, not a sixteenth-note subdivision.</summary>
    private const int BeatsPerNote = 1;

    /// <summary>Notes in a generated phrase. Long enough to show a shape, short enough to sing in one
    /// breath — the phrase profile the analyzer measures on real songs averages near this.</summary>
    private const int PhraseNotes = 8;

    /// <summary>Mic input is what gets graded, so this is a placeholder; the collector fixes its own.</summary>
    private const int Velocity = 90;

    private const double MinBpm = 40.0;
    private const double MaxBpm = 200.0;

    /// <summary>Singable range for an untrained voice; the requested octave is clamped into it.</summary>
    private const int MinOctave = 2;
    private const int MaxOctave = 5;

    public static ModeExerciseDto Build(
        ModeExerciseKind kind, ScaleMode mode, int tonicPitchClass, double bpm, int seed, int octave)
    {
        var tonic = ((tonicPitchClass % 12) + 12) % 12;
        var tempo = Math.Clamp(bpm, MinBpm, MaxBpm);
        var register = Math.Clamp(octave, MinOctave, MaxOctave);
        var root = ((register + 1) * 12) + tonic;
        var intervals = ScaleModes.Intervals(mode);

        var pitches = kind switch
        {
            ModeExerciseKind.ScaleRun => ScaleRun(root, intervals),
            ModeExerciseKind.CharacteristicLeap => CharacteristicLeap(root, mode),
            ModeExerciseKind.Phrase => Phrase(root, intervals, seed),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled exercise kind."),
        };

        var secondsPerNote = 60.0 / tempo * BeatsPerNote;
        var notes = pitches
            .Select((pitch, index) => new NoteEvent(pitch, index * secondsPerNote, secondsPerNote, Velocity))
            .ToList();

        var characteristics = ScaleModes.CharacteristicIntervals(mode)
            .Select(PitchNames.IntervalLabel)
            .ToList();

        return new ModeExerciseDto(
            Kind: kind,
            Title: Title(kind, tonic, mode),
            Instruction: Instruction(kind, tonic, mode),
            Mode: mode,
            TonicPitchClass: tonic,
            TonicName: PitchNames.Name(tonic),
            Bpm: tempo,
            Seed: seed,
            Octave: register,
            ScaleNotes: ScaleModes.NoteNames(tonic, mode),
            CharacteristicDegrees: characteristics,
            TargetNotes: notes,
            DurationSec: notes.Count * secondsPerNote);
    }

    /// <summary>Up the scale to the octave and back down, without sounding the top note twice.</summary>
    private static List<int> ScaleRun(int root, IReadOnlyList<int> intervals)
    {
        var up = intervals.Select(interval => root + interval).Append(root + 12).ToList();
        var down = up.AsEnumerable().Reverse().Skip(1);
        return [.. up, .. down];
    }

    /// <summary>
    /// Tonic, the degree that names the mode, tonic — once per characteristic degree.
    ///
    /// <para>This is the drill the whole feature exists for. Dorian and Aeolian share six notes out of
    /// seven, so singing the scale proves almost nothing about hearing Dorian; singing the natural
    /// 6th against the tonic and hearing it as home-relative is the entire difference. Returning to
    /// the tonic after each one is what makes it an interval rather than a note.</para>
    /// </summary>
    private static List<int> CharacteristicLeap(int root, ScaleMode mode)
    {
        var pitches = new List<int>();
        foreach (var interval in ScaleModes.CharacteristicIntervals(mode))
        {
            pitches.Add(root);
            pitches.Add(root + interval);
            pitches.Add(root);
        }
        // A mode with a single characteristic degree yields three notes, which is a gesture rather
        // than an exercise; the octave answer gives the ear somewhere to finish.
        pitches.Add(root + 12);
        pitches.Add(root);
        return pitches;
    }

    /// <summary>
    /// A seeded walk over the pool that starts and ends on the tonic and prefers steps to leaps.
    ///
    /// <para>Weighted towards adjacent scale degrees because that is what melodies actually do — the
    /// motion profile the analyzer measures on real songs is step-dominated — and because a phrase of
    /// random leaps tests sight-singing rather than whether the mode is in the ear.</para>
    /// </summary>
    private static List<int> Phrase(int root, IReadOnlyList<int> intervals, int seed)
    {
        var random = new Random(seed);
        var degrees = intervals.Count;
        var pitches = new List<int> { root };
        var degree = 0;

        for (var step = 1; step < PhraseNotes - 1; step++)
        {
            // Step two thirds of the time, leap a third of it: enough motion to be a phrase, not
            // enough to be an interval test.
            var move = random.NextDouble() < 0.67 ? 1 : 2;
            if (random.Next(2) == 0)
            {
                move = -move;
            }
            // Bounded an octave either side of the tonic so a run of same-direction moves cannot
            // walk the phrase out of the singer's range.
            degree = Math.Clamp(degree + move, -degrees, degrees);
            pitches.Add(PitchAt(root, intervals, degree));
        }

        pitches.Add(root);
        return pitches;
    }

    /// <summary>
    /// A scale degree that may run past either end of the interval table, wrapped into the octave
    /// above or below. Lets the walk breathe past the tonic without leaving the mode.
    /// </summary>
    private static int PitchAt(int root, IReadOnlyList<int> intervals, int degree)
    {
        var count = intervals.Count;
        var octaveShift = (int)Math.Floor(degree / (double)count);
        var index = degree - (octaveShift * count);
        return root + intervals[index] + (octaveShift * 12);
    }

    private static string Title(ModeExerciseKind kind, int tonic, ScaleMode mode)
    {
        var name = $"{PitchNames.Name(tonic)} {mode}";
        return kind switch
        {
            ModeExerciseKind.ScaleRun => $"{name} — up and down",
            ModeExerciseKind.CharacteristicLeap => $"{name} — the degree that names it",
            ModeExerciseKind.Phrase => $"{name} — sing the phrase",
            _ => name,
        };
    }

    /// <summary>
    /// What the exercise is asking for, in one sentence. Worded here rather than in the client
    /// because naming a degree and saying what it does to a mode is a musical statement, and the
    /// server is the only place allowed to make those.
    /// </summary>
    private static string Instruction(ModeExerciseKind kind, int tonic, ScaleMode mode)
    {
        var tonicName = PitchNames.Name(tonic);
        var degrees = ScaleModes.CharacteristicIntervals(mode);
        var degreeText = string.Join(" and the ", degrees.Select(PitchNames.IntervalLabel));
        var degreeNotes = string.Join(" and ", degrees.Select(interval => PitchNames.Name(tonic + interval)));

        return kind switch
        {
            ModeExerciseKind.ScaleRun =>
                $"Sing {mode} on {tonicName}, one note per click, up to the octave and back down. "
                + $"Listen for the {degreeText} on the way past — that is the degree this mode is named for.",
            ModeExerciseKind.CharacteristicLeap =>
                $"Leap from {tonicName} to the {degreeText} ({degreeNotes}) and back, one note per click. "
                + $"{mode} shares nearly every note with its neighbours; this interval is what separates "
                + "them, so hearing it against the tonic is the whole exercise.",
            ModeExerciseKind.Phrase =>
                $"Sing this phrase in {tonicName} {mode}, one note per click. It uses only the mode's own "
                + $"notes, and it lands on {tonicName} so you can hear whether that note feels like home.",
            _ => $"Sing {tonicName} {mode}, one note per click.",
        };
    }
}
