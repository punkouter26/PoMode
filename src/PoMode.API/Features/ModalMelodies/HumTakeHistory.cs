using PoMode.API.Features.Analysis;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.SongStatistics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalMelodies;

/// <summary>
/// What a singer's earlier hum takes say, read back to them: which modes their melodies came out in
/// over one backing, and where their voice sits across all of them. Both are derived on demand from
/// the jobs the caller owns and nothing is persisted, the same ruling as <c>/stats</c>.
///
/// <para>Neither is a score, and the wording keeps it that way. The history tallies what the analyzer
/// found without ranking it against the mode the chords were built from, so it has no "best take", no
/// run of matches and no percentage. Practice is unscored on purpose: a take that came out Mixolydian
/// over Dorian chords is a finding, not a miss.</para>
/// </summary>
public sealed class HumTakeHistory(JobStore store, ModalMelodyGenerator generator)
{
    /// <summary>Fewer finished takes than this and the range is one session's mood, not a voice.</summary>
    public const int MinTakes = 3;

    /// <summary>Pooled notes needed before a percentile means anything. Three short takes can clear
    /// <see cref="MinTakes"/> with a dozen notes between them.</summary>
    public const int MinNotes = 40;

    /// <summary>A take that yielded fewer notes than this recorded silence or a cough, and counting it
    /// towards <see cref="MinTakes"/> would claim more data than there is.</summary>
    private const int MinNotesPerTake = 5;

    /// <summary>A range is a present-tense fact — a voice warms up, recovers from a cold, gets trained
    /// — so only the most recent takes speak for it.</summary>
    private const int MaxTakes = 20;

    /// <summary>Shorter detections are breath, consonants and onset glitches from the pitch tracker
    /// rather than pitches the singer held. A sixteenth at 150 BPM is still 0.1s.</summary>
    private const double MinNoteSec = 0.1;

    /// <summary>
    /// The comfortable span is the 10th to 90th percentile of sung notes, not the extremes: a pitch
    /// tracker's octave slip or one strained top note would otherwise set the range a key is chosen
    /// for. Same rule as the per-song tessitura, so the two figures mean the same thing.
    /// </summary>
    private const double LowPercentile = 0.10;
    private const double HighPercentile = 0.90;

    /// <summary>Half an octave. The fit puts the mode's home note this far below where the voice
    /// centres, so the octave from home to home — the span a modal melody orbits — sits across the
    /// middle of the range rather than hanging off either end of it.</summary>
    private const int HomeBelowCentre = 6;

    /// <summary>
    /// The caller's takes over one backing, newest first.
    /// </summary>
    /// <remarks>
    /// Matched on mode, progression and purity, and deliberately not on key or tempo. Mode and
    /// progression are the question being asked ("what do I sing over these chords?"). Purity is in
    /// because it rotates the loop off its home chord, so the same id at a different purity is a
    /// different sequence of chords. Key is out because a transposed backing asks the identical
    /// modal question in another register — and "Fit to my voice" exists to change it, which would
    /// otherwise wipe the history the moment it was used. Tempo is out because slowing the loop down
    /// to learn it changes nothing about the harmony under the melody. Each row still names the key
    /// and tempo it was sung at, so nothing that did vary is hidden.
    /// </remarks>
    public async Task<TakeHistoryDto> ForBackingAsync(
        string? ownerId, ModalMelodyRequest backing, CancellationToken ct)
    {
        var progression = generator.GetProgression(backing.ProgressionId);
        var matching = (await OwnedTakesAsync(ownerId, ct))
            .Where(state => state.Origin?.Backing is { } b
                && b.Mode == backing.Mode
                && string.Equals(b.ProgressionId, progression.Id, StringComparison.OrdinalIgnoreCase)
                && b.TargetPurity.Equals(backing.TargetPurity))
            .ToList();
        return Summarise(progression, backing.Mode, matching);
    }

    /// <summary>The caller's vocal range from their recent finished takes, over any backing — a voice
    /// does not change register with the chords — plus the key that fits <paramref name="mode"/> to it.</summary>
    public async Task<VocalRangeDto> VocalRangeAsync(string? ownerId, ScaleMode mode, CancellationToken ct)
    {
        var takes = new List<IReadOnlyList<NoteEvent>>();
        foreach (var state in await OwnedTakesAsync(ownerId, ct))
        {
            if (state.Stage != JobStage.Complete)
            {
                continue;
            }
            takes.Add(await store.ReadArtifactListAsync<NoteEvent>(state.JobId, "notes.json", ct));
            if (takes.Count == MaxTakes)
            {
                break;
            }
        }
        return Profile(takes, mode);
    }

    /// <summary>Every hum take the caller owns, newest first. Ownerless callers own nothing — same
    /// rule as the library, whose scan this mirrors.</summary>
    private async Task<List<JobState>> OwnedTakesAsync(string? ownerId, CancellationToken ct)
    {
        var takes = new List<JobState>();
        if (ownerId is null)
        {
            return takes;
        }
        foreach (var jobId in store.ListJobIds())
        {
            if (!JobId.IsValid(jobId))
            {
                continue;
            }
            if (await store.LoadAsync(jobId, ct) is { } state
                && state.OwnerId == ownerId
                && state.Origin?.Kind == TakeOrigin.HumTake)
            {
                takes.Add(state);
            }
        }
        return [.. takes.OrderByDescending(state => state.CreatedAt)];
    }

    /// <summary>
    /// The rows and the one sentence over them. A cancelled take is left out — the singer threw it
    /// away before it was read — but a failed one stays, because leaving it out would make the tally
    /// quietly disagree with the number of times they sang.
    /// </summary>
    public static TakeHistoryDto Summarise(
        ChordProgressionDefinition progression, ScaleMode mode, IReadOnlyList<JobState> takes)
    {
        var rows = takes
            .Where(state => state.Stage != JobStage.Cancelled)
            .OrderByDescending(state => state.CreatedAt)
            .Select(ToRow)
            .ToList();

        // The roman numerals rather than the preset's display name, whose parenthetical spells the
        // chords in C and would be wrong for every take sung in another key.
        var over = $"{progression.Name.Split(" (")[0]} ({progression.RomanNumerals}) in {ModeName(mode)}";
        if (rows.Count == 0)
        {
            return new TakeHistoryDto($"No takes over {over} yet.", rows);
        }

        // Most common reading first only so the sentence reads naturally; nothing is marked as the
        // expected answer, and the backing's own mode gets no special place in the list.
        var parts = rows
            .Where(row => row.FoundMode is not null)
            .GroupBy(row => row.FoundMode!)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select((group, index) => index == 0
                ? $"{group.Count()} came out {group.Key}"
                : $"{group.Count()} {group.Key}")
            .ToList();

        var unclear = rows.Count(row => row.Stage == JobStage.Complete && row.FoundMode is null);
        var pending = rows.Count(row => !row.Stage.IsTerminal());
        var failed = rows.Count(row => row.Stage == JobStage.Failed);
        if (unclear > 0) parts.Add($"{unclear} with no clear mode");
        if (pending > 0) parts.Add($"{pending} still being read");
        if (failed > 0) parts.Add($"{failed} could not be read");

        var noun = rows.Count == 1 ? "take" : "takes";
        return new TakeHistoryDto($"{rows.Count} {noun} over {over}: {string.Join(", ", parts)}.", rows);
    }

    private static PastTakeDto ToRow(JobState state)
    {
        // PrimaryMode is stamped on the job at completion, so a finished take with none is a take the
        // engine found no clear mode in — not one waiting on a result.json read.
        var foundMode = state.Stage == JobStage.Complete && state.PrimaryMode is { } primary
            ? ModeName(primary)
            : null;
        var reading = state.Stage switch
        {
            JobStage.Complete when foundMode is not null => $"{state.TonicName} {foundMode}".Trim(),
            JobStage.Complete => "No clear mode",
            JobStage.Failed => "Could not be read",
            _ => "Still being read",
        };
        return new PastTakeDto(state.JobId, state.CreatedAt, state.Stage, foundMode, reading, SungOver(state.Origin?.Backing));
    }

    /// <summary>The backing's own key and tempo, named by its mode root the way the take's origin
    /// sentence names it — the note the chords treat as home.</summary>
    private static string SungOver(ModalMelodyRequest? backing)
    {
        if (backing is null)
        {
            return "";
        }
        var root = PitchNames.Name(backing.TonicPitchClass + ScaleModes.ModeDegreeOffset(backing.Mode));
        return $"over {root} {ModeName(backing.Mode)} at {backing.Bpm:0} BPM";
    }

    /// <summary>
    /// Pools the notes of the given takes into a range, and fits <paramref name="mode"/> to it once
    /// there is enough to go on. With too little, it says how many more takes would do rather than
    /// guessing a range from one lucky phrase.
    /// </summary>
    public static VocalRangeDto Profile(IReadOnlyList<IReadOnlyList<NoteEvent>> takes, ScaleMode mode)
    {
        var usable = takes
            .Select(take => take.Where(note => note.DurationSec >= MinNoteSec).ToList())
            .Where(take => take.Count >= MinNotesPerTake)
            .ToList();
        // One note, one vote: weighting by duration would let a single held note define the range.
        var pitches = usable.SelectMany(take => take.Select(note => note.MidiPitch)).Order().ToArray();

        var needed = usable.Count < MinTakes ? MinTakes - usable.Count
            : pitches.Length < MinNotes ? 1
            : 0;
        if (needed > 0)
        {
            var more = usable.Count == 0 ? $"{needed}" : $"{needed} more";
            var soFar = usable.Count == 0 ? "" : $" ({usable.Count} so far)";
            return new VocalRangeDto(usable.Count, pitches.Length, needed, null, null, null, null,
                $"Sing {more} {(needed == 1 ? "take" : "takes")} over any chords to see your range and a key that fits it{soFar}.",
                null);
        }

        var low = SongStatsBuilder.Percentile(pitches, LowPercentile);
        var high = SongStatsBuilder.Percentile(pitches, HighPercentile);
        var lowLabel = SongStatsBuilder.PitchLabel(low);
        var highLabel = SongStatsBuilder.PitchLabel(high);
        return new VocalRangeDto(
            usable.Count, pitches.Length, 0, low, lowLabel, high, highLabel,
            $"Your range: {lowLabel}–{highLabel} (from {usable.Count} takes).",
            Fit(mode, SongStatsBuilder.Percentile(pitches, 0.5)));
    }

    /// <summary>
    /// The parent key that puts <paramref name="mode"/>'s home note half an octave below the middle of
    /// the voice. Centred on the median rather than on the midpoint of the range, because the median
    /// is where the singer actually spends their time; a range stretched by a few high notes should not
    /// drag every key upward.
    /// </summary>
    private static VoiceFitDto Fit(ScaleMode mode, int centreMidi)
    {
        var home = centreMidi - HomeBelowCentre;
        var homeClass = ((home % 12) + 12) % 12;
        var parentKey = ((homeClass - ScaleModes.ModeDegreeOffset(mode)) % 12 + 12) % 12;
        var explanation =
            $"Key of {PitchNames.Name(parentKey)}: {PitchNames.Name(homeClass)} {ModeName(mode)} puts its home note on "
            + $"{SongStatsBuilder.PitchLabel(home)} and the octave on {SongStatsBuilder.PitchLabel(home + 12)}, "
            + "around the middle of your range.";
        return new VoiceFitDto(mode, parentKey, explanation);
    }

    private static string ModeName(ScaleMode mode) => ModeName(mode.ToString());

    /// <summary>"MinorPentatonic" as a reader would write it. The analyzer stamps the enum name.</summary>
    private static string ModeName(string mode)
        => string.Concat(mode.Select((c, i) => i > 0 && char.IsUpper(c) ? $" {c}" : $"{c}"));
}
