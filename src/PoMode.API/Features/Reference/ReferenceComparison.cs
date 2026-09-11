using System.Globalization;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Reference;

/// <summary>
/// Compares what PoMode measured with what a public catalogue says, and writes the one sentence that
/// puts the two readings next to each other.
///
/// <para>Pure, and separate from the network client, because this is the part with an opinion in it.
/// The comparison is not a grading: a catalogue's key estimate is another program's guess, made by a
/// classifier that only ever answers "major" or "minor". So the commonest way for the two to
/// "disagree" is for the catalogue to name the relative major of a modal reading — F major under a
/// melody centred on D — and that is not a contradiction, it is precisely the distinction this app
/// exists to draw. Saying so is more useful than a red cross.</para>
/// </summary>
public static class ReferenceComparison
{
    /// <summary>Within this fraction the two tempo estimates are the same number.</summary>
    private const double TempoTolerance = 0.03;

    /// <summary>How near a doubling or halving still counts as the same pulse counted differently.</summary>
    private const double HalfTimeTolerance = 0.06;

    /// <summary>Modes whose third is major. A catalogue classifier only answers major or minor, so
    /// this is the whole of the vocabulary its answer can be compared against.</summary>
    private static readonly ScaleMode[] MajorLike =
        [ScaleMode.Ionian, ScaleMode.Lydian, ScaleMode.Mixolydian, ScaleMode.MajorPentatonic];

    private static readonly Dictionary<string, int> PitchClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["C"] = 0, ["B#"] = 0,
        ["C#"] = 1, ["Db"] = 1,
        ["D"] = 2,
        ["D#"] = 3, ["Eb"] = 3,
        ["E"] = 4, ["Fb"] = 4,
        ["F"] = 5, ["E#"] = 5,
        ["F#"] = 6, ["Gb"] = 6,
        ["G"] = 7,
        ["G#"] = 8, ["Ab"] = 8,
        ["A"] = 9,
        ["A#"] = 10, ["Bb"] = 10,
        ["B"] = 11, ["Cb"] = 11,
    };

    /// <summary>Semitone parse of a note name, or null when it is not one.</summary>
    public static int? PitchClass(string? noteName)
        => noteName is not null && PitchClasses.TryGetValue(noteName.Trim(), out var pitchClass)
            ? pitchClass
            : null;

    public static (ReferenceAgreement Key, ReferenceAgreement Tempo, string Sentence) Compare(
        string measuredTonic,
        ScaleMode? measuredMode,
        double measuredBpm,
        string? referenceKey,
        string? referenceScale,
        double? referenceBpm)
    {
        var keyAgreement = CompareKey(measuredTonic, measuredMode, referenceKey, referenceScale);
        var tempoAgreement = CompareTempo(measuredBpm, referenceBpm);

        var parts = new List<string>();
        if (KeySentence(measuredTonic, measuredMode, referenceKey, referenceScale, keyAgreement) is { } key)
        {
            parts.Add(key);
        }
        if (TempoSentence(measuredBpm, referenceBpm, tempoAgreement) is { } tempo)
        {
            parts.Add(tempo);
        }

        return (keyAgreement, tempoAgreement, parts.Count == 0
            // The commonest outcome by far: the recording is in the catalogue but nobody ever
            // submitted an analysis of it. An absence, stated as one.
            ? "The catalogue has this recording but no community key or tempo analysis to compare against."
            : string.Join(" ", parts));
    }

    private static ReferenceAgreement CompareKey(
        string measuredTonic, ScaleMode? measuredMode, string? referenceKey, string? referenceScale)
    {
        if (PitchClass(referenceKey) is not { } reference || PitchClass(measuredTonic) is not { } measured)
        {
            return ReferenceAgreement.Unknown;
        }
        if (measuredMode is null)
        {
            // No mode was determined, so the tonic is all there is to compare — and comparing it
            // alone is still worth something.
            return reference == measured ? ReferenceAgreement.Agrees : ReferenceAgreement.Differs;
        }
        if (reference != measured)
        {
            return ReferenceAgreement.Differs;
        }

        // Same tonic: the two only truly agree if they also agree about the third, since "D major"
        // and "D Dorian" are different claims about the same home note.
        var referenceIsMajor = IsMajorScale(referenceScale);
        return referenceIsMajor is null || referenceIsMajor == MajorLike.Contains(measuredMode.Value)
            ? ReferenceAgreement.Agrees
            : ReferenceAgreement.Differs;
    }

    private static ReferenceAgreement CompareTempo(double measuredBpm, double? referenceBpm)
    {
        if (referenceBpm is not { } reference || reference <= 0 || measuredBpm <= 0)
        {
            return ReferenceAgreement.Unknown;
        }
        return Math.Abs(reference - measuredBpm) / measuredBpm <= TempoTolerance
            ? ReferenceAgreement.Agrees
            : ReferenceAgreement.Differs;
    }

    private static string? KeySentence(
        string measuredTonic,
        ScaleMode? measuredMode,
        string? referenceKey,
        string? referenceScale,
        ReferenceAgreement agreement)
    {
        if (agreement == ReferenceAgreement.Unknown)
        {
            return null;
        }

        var mine = measuredMode is null ? measuredTonic : $"{measuredTonic} {measuredMode}";
        var theirs = $"{referenceKey}{(IsMajorScale(referenceScale) is { } major ? major ? " major" : " minor" : "")}";

        if (agreement == ReferenceAgreement.Agrees)
        {
            return $"The catalogue calls it {theirs} and PoMode measured {mine} — the same home note.";
        }

        // The informative disagreement: same seven notes, different note treated as home. Naming it
        // is the point, because a reader who sees only "differs" learns nothing from it.
        if (RelativeReading(measuredTonic, measuredMode, referenceKey, referenceScale) is { } relative)
        {
            return $"The catalogue calls it {theirs}; PoMode measured {mine}. Those are the same seven "
                + $"notes — {theirs} is the {relative} of this reading — so the disagreement is about "
                + "which note the music treats as home, not about the notes themselves.";
        }

        return $"The catalogue calls it {theirs}; PoMode measured {mine}. The two readings disagree.";
    }

    /// <summary>
    /// "relative major" / "relative minor" when the catalogue's key is this reading's relative, else
    /// null. A minor-third above a minor-ish reading is its relative major, and a major sixth above a
    /// major-ish reading is its relative minor.
    /// </summary>
    private static string? RelativeReading(
        string measuredTonic, ScaleMode? measuredMode, string? referenceKey, string? referenceScale)
    {
        if (measuredMode is not { } mode
            || PitchClass(measuredTonic) is not { } measured
            || PitchClass(referenceKey) is not { } reference
            || IsMajorScale(referenceScale) is not { } referenceIsMajor)
        {
            return null;
        }

        var measuredIsMajor = MajorLike.Contains(mode);
        if (!measuredIsMajor && referenceIsMajor && reference == (measured + 3) % 12)
        {
            return "relative major";
        }
        if (measuredIsMajor && !referenceIsMajor && reference == (measured + 9) % 12)
        {
            return "relative minor";
        }
        return null;
    }

    private static string? TempoSentence(
        double measuredBpm, double? referenceBpm, ReferenceAgreement agreement)
    {
        if (agreement == ReferenceAgreement.Unknown || referenceBpm is not { } reference)
        {
            return null;
        }

        var mine = measuredBpm.ToString("0", CultureInfo.InvariantCulture);
        var theirs = reference.ToString("0", CultureInfo.InvariantCulture);

        if (agreement == ReferenceAgreement.Agrees)
        {
            return $"Both put the tempo near {mine} BPM.";
        }

        // A doubling is not a disagreement about speed, it is a disagreement about what counts as a
        // beat — and a listener who is told "119 vs 238" without that will assume one of them is broken.
        var ratio = reference / measuredBpm;
        if (Math.Abs(ratio - 2.0) <= HalfTimeTolerance * 2 || Math.Abs(ratio - 0.5) <= HalfTimeTolerance)
        {
            return $"The catalogue says {theirs} BPM against PoMode's {mine} — one is counting the beat "
                + "at double the other's, which is the same pulse felt two ways.";
        }

        return $"The catalogue says {theirs} BPM; PoMode measured {mine}.";
    }

    /// <summary>True for major, false for minor, null when the catalogue said neither.</summary>
    private static bool? IsMajorScale(string? scale) => scale?.Trim().ToLowerInvariant() switch
    {
        "major" => true,
        "minor" => false,
        _ => null,
    };
}
