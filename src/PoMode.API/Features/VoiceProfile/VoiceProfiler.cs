using PoMode.API.Features.SongStatistics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.VoiceProfile;

/// <summary>One sung note with how well it was held: <see cref="OffCents"/> is its median distance
/// from true pitch (signed, sharp positive), <see cref="WobbleCents"/> how far the pitch moved while
/// it was held. Both null when the audio could not be re-measured for that note.</summary>
public sealed record NoteIntonation(int MidiPitch, double DurationSec, double? OffCents, double? WobbleCents);

/// <summary>Whose voice the sentences are about: a song's singer, or the person reading them.</summary>
public enum VoiceSubject
{
    Song,
    You,
}

/// <summary>
/// Turns sung notes into the two things a singer asks about their voice: what type it is, and which
/// note it does best. Pure — the audio has already been measured by <see cref="IntonationMeter"/>.
///
/// <para>The type is judged by range, which is what a pitch track can see. A teacher also listens to
/// tone and to where the voice changes register; the sentence says so rather than implying a
/// classification it could not have made, and the labels name a range, never the person.</para>
/// </summary>
public static class VoiceProfiler
{
    /// <summary>Shorter detections are breath, consonants and tracker glitches, not held pitches.</summary>
    private const double MinNoteSec = 0.1;

    /// <summary>Below either of these the median is one phrase's mood rather than a voice.</summary>
    private const int MinNotes = 20;
    private const double MinSungSec = 12.0;

    /// <summary>A type is a candidate when this share of the notes fits inside its classical range.
    /// Not 100%: one octave slip from the tracker should not disqualify the right answer.</summary>
    private const double MinCoverage = 0.85;

    /// <summary>When the two nearest types' centres are this close to the voice's centre, it is
    /// honestly between them, and saying only one would overstate a pitch track's resolution.</summary>
    private const double BetweenSemitones = 1.5;

    /// <summary>A pitch sung fewer times or for less time than this is a passing note, not a
    /// candidate for the note the voice does best, however cleanly it happened to land.</summary>
    private const int MinStrongestCount = 3;
    private const double MinStrongestSec = 1.0;

    /// <summary>
    /// Classical ranges (MIDI) and where each type usually spends its time. The centre sits about two
    /// semitones below the middle of the range: singers live in the lower middle of their voice and
    /// visit the top, so matching a median against the arithmetic middle would read every baritone
    /// as a bass.
    /// </summary>
    private static readonly (VoiceType Type, string Label, int Low, int High, int Centre)[] Types =
    [
        (VoiceType.Bass, "Bass", 40, 64, 50),          // E2–E4
        (VoiceType.Baritone, "Baritone", 45, 69, 55),  // A2–A4
        (VoiceType.Tenor, "Tenor", 48, 72, 58),        // C3–C5
        (VoiceType.Alto, "Alto", 53, 77, 63),          // F3–F5
        (VoiceType.MezzoSoprano, "Mezzo-soprano", 57, 81, 67), // A3–A5
        (VoiceType.Soprano, "Soprano", 60, 84, 70),    // C4–C6
    ];

    private const string HowJudged =
        "Judged from pitch alone: where the singing sits, and how long, how close to true pitch and how "
        + "steadily each note is held. A voice teacher also listens to tone and to where the voice changes "
        + "register, which a pitch track cannot hear.";

    public static VoiceProfileDto Declined(string summary)
        => new(summary, null, null, null, null, null, null, null, [], HowJudged);

    public static VoiceProfileDto Build(IReadOnlyList<NoteIntonation> sung, VoiceSubject subject)
    {
        var notes = sung.Where(note => note.DurationSec >= MinNoteSec).ToList();
        var sungSec = notes.Sum(note => note.DurationSec);
        if (notes.Count < MinNotes || sungSec < MinSungSec)
        {
            return Declined(
                $"Too little held singing to judge a voice yet ({notes.Count} notes over {sungSec:0} s; "
                + $"it needs about {MinNotes} notes over {MinSungSec:0} s).");
        }

        // One note, one vote, as in the tessitura: weighting by duration would let one long final
        // note decide the type.
        var pitches = notes.Select(note => note.MidiPitch).Order().ToArray();
        var low = SongStatsBuilder.Percentile(pitches, 0.10);
        var median = SongStatsBuilder.Percentile(pitches, 0.50);
        var high = SongStatsBuilder.Percentile(pitches, 0.90);

        var ranked = Types
            .Select(type => (type, Coverage: pitches.Count(p => p >= type.Low && p <= type.High) / (double)pitches.Length,
                Distance: Math.Abs(median - type.Centre)))
            .ToList();
        var candidates = ranked.Where(r => r.Coverage >= MinCoverage).OrderBy(r => r.Distance).ToList();
        if (candidates.Count == 0)
        {
            // Wider than any one type: say the type most of it fits rather than nothing.
            candidates = [.. ranked.OrderByDescending(r => r.Coverage).ThenBy(r => r.Distance)];
        }
        var best = candidates[0];
        var leaning = candidates.Count > 1 && candidates[1].Distance - best.Distance < BetweenSemitones
            ? candidates[1].type.Label
            : null;

        var tessitura = $"{SongStatsBuilder.PitchLabel(low)}–{SongStatsBuilder.PitchLabel(high)}, centred on {SongStatsBuilder.PitchLabel(median)}";
        var who = subject == VoiceSubject.You ? "Your singing" : "The singing";
        var where = $"most notes fall between {SongStatsBuilder.PitchLabel(low)} and {SongStatsBuilder.PitchLabel(high)}, "
                    + $"centred on {SongStatsBuilder.PitchLabel(median)}.";
        var summary = leaning is not null
            ? $"{who} sits between {Article(best.type.Label)} and {Article(leaning)} range, closer to "
              + $"{best.type.Label.ToLowerInvariant()}: {where}"
            : $"{who} sits in {Article(best.type.Label)} range: {where}";

        var byPitch = ByPitch(notes);
        var strongest = Strongest(byPitch, subject);
        return new VoiceProfileDto(
            summary,
            best.type.Type,
            best.type.Label,
            leaning,
            tessitura,
            strongest?.Note.Midi,
            strongest?.Note.Label,
            strongest?.Sentence,
            [.. byPitch.Where(Qualifies).OrderByDescending(Score).Take(5).OrderBy(n => n.Midi)],
            HowJudged);
    }

    /// <summary>Every sung pitch pooled over its notes. Tuning and wobble are duration-weighted over the
    /// notes that could be measured, so a long held note counts for more than a grace note.</summary>
    private static List<VoiceNoteDto> ByPitch(IEnumerable<NoteIntonation> notes)
        => [.. notes.GroupBy(note => note.MidiPitch).Select(group =>
        {
            var measured = group.Where(n => n.OffCents is not null).ToList();
            var weight = measured.Sum(n => n.DurationSec);
            double? off = weight > 0 ? measured.Sum(n => Math.Abs(n.OffCents!.Value) * n.DurationSec) / weight : null;
            var steady = measured.Where(n => n.WobbleCents is not null).ToList();
            var steadyWeight = steady.Sum(n => n.DurationSec);
            double? wobble = steadyWeight > 0 ? steady.Sum(n => n.WobbleCents!.Value * n.DurationSec) / steadyWeight : null;
            return new VoiceNoteDto(group.Key, SongStatsBuilder.PitchLabel(group.Key), group.Count(),
                group.Sum(n => n.DurationSec), off, wobble);
        })];

    private static bool Qualifies(VoiceNoteDto note)
        => note.Count >= MinStrongestCount && note.HeldSec >= MinStrongestSec;

    /// <summary>
    /// Time held, discounted by how far off and how unsteady the note was. The falloffs are gentle on
    /// purpose: 10 cents off keeps about three quarters of the credit, 30 cents about a third, so a note
    /// sung a great deal and a little flat can still beat one sung twice and perfectly.
    /// </summary>
    private static double Score(VoiceNoteDto note)
        => note.HeldSec
           * Math.Exp(-(note.OffCents ?? 0) / 30.0)
           * Math.Exp(-(note.WobbleCents ?? 0) / 40.0);

    private static (VoiceNoteDto Note, string Sentence)? Strongest(List<VoiceNoteDto> byPitch, VoiceSubject subject)
    {
        var qualified = byPitch.Where(Qualifies).ToList();
        if (qualified.Count == 0)
        {
            return null;
        }
        var best = qualified.MaxBy(Score)!;
        var held = $"{best.HeldSec:0.0} s over {best.Count} notes";
        var tuned = best.OffCents is { } off
            ? $"{off:0} {(Math.Round(off) == 1 ? "cent" : "cents")} from true pitch on average"
            : null;

        // Only superlatives that are true go in the sentence; when the note wins on balance rather than
        // on any single measure, the sentence says that instead of borrowing a "most" it did not earn.
        var you = subject == VoiceSubject.You;
        var claims = new List<string>();
        if (best.HeldSec >= qualified.Max(n => n.HeldSec))
        {
            claims.Add(you ? $"hold it longest ({held})" : $"held longest ({held})");
        }
        if (tuned is not null && best.OffCents <= qualified.Where(n => n.OffCents is not null).Min(n => n.OffCents))
        {
            claims.Add(you ? $"land it closest to true pitch ({tuned})" : $"landed closest to true pitch ({tuned})");
        }
        if (best.WobbleCents is not null && best.WobbleCents <= qualified.Where(n => n.WobbleCents is not null).Min(n => n.WobbleCents))
        {
            claims.Add(you ? "hold it steadiest" : "held steadiest");
        }

        var opener = you ? $"Your strongest note is {best.Label}" : $"The strongest note is {best.Label}";
        var sentence = claims.Count > 0
            ? $"{opener}: {(you ? "you" : "it is")} {JoinAnd(claims)}."
            : tuned is not null
                ? $"{opener}: the best balance of time held ({held}), tuning ({tuned}) and steadiness."
                : $"{opener}: the most time held ({held}).";
        return (best, sentence);
    }

    private static string JoinAnd(List<string> parts)
        => parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";

    private static string Article(string label)
        => ("AEIOU".Contains(char.ToUpperInvariant(label[0])) ? "an " : "a ") + label.ToLowerInvariant();
}
