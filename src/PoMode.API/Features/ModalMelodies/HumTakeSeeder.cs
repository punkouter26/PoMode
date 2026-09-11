using PoMode.API.Features.Analysis;
using PoMode.API.Features.Audio;
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalMelodies;

/// <summary>
/// Prepares a hum take for the normal pipeline by handing it the two things it would otherwise have
/// to guess, and that a solo hum cannot actually support guessing.
///
/// <para>The chords are the first. The user sang over a progression the Mode Lab generated, so the
/// harmony under the take is a setting they chose — running a chord recognizer over a monophonic
/// hum would invent a chord track that was never sung and then analyze the mode against it. The
/// recording holds one voice; the harmony it belongs to lives here.</para>
///
/// <para>The tempo is the second, for the same reason: the loop played at the BPM on the slider, so
/// beats.json is written from that number rather than estimated off a hum with no percussion in it.</para>
///
/// <para>Separation is skipped outright — the take <em>is</em> a dry vocal, so there is nothing to
/// pull apart, and every later stage already falls back to the upload when no stems exist. The
/// pipeline needs no special case for any of this: pre-completed stages with their artifacts on
/// disk are exactly the shape it already restarts from.</para>
/// </summary>
public sealed class HumTakeSeeder(ModalMelodyGenerator generator, JobStore store, TimeProvider time)
{
    /// <summary>Recorded in the plan and the stage history in place of the executor that did not run,
    /// so the UI never credits a recognizer with a chord track it never detected.</summary>
    private const string ProvidedChordsExecutor = "ModeLabBacking";

    private const string SkippedSeparationExecutor = "SkippedDryVocal";

    /// <summary>Matches the live endpoint's floor: a sliver of a chord at the end of a take is not a
    /// window worth scoring a mode over.</summary>
    private const double MinSpanSeconds = 0.25;

    /// <summary>A take is capped long before this, but the tiling loop must terminate on its own
    /// rather than trust that.</summary>
    private const int MaxSpans = 4096;

    public async Task SeedAsync(JobState state, ModalMelodyRequest backing, CancellationToken ct)
    {
        // Deterministic: the same request the browser played the loop from regenerates the identical
        // chords here, which is what lets the take carry a reference to its backing instead of a copy.
        var generated = generator.Generate(backing);
        var loopSeconds = generated.Chords.Count > 0 ? generated.Chords[^1].EndSec : 0.0;
        if (loopSeconds <= 0.0)
        {
            // No harmony to hand over — leave the job to run the ordinary way rather than seed it
            // with an empty chord track that would silently starve the mode engine of windows.
            return;
        }

        var takeSeconds = AudioDecoder.TryReadDurationSeconds(store.InputPath(state)) ?? loopSeconds;
        var chords = TileOverTake(generated.Chords, loopSeconds, takeSeconds);
        if (chords.Count == 0)
        {
            return;
        }

        await store.WriteArtifactAsync(state.JobId, "chords.json", chords, ct);
        // Confidence 1.0 is not optimism: the loop was scheduled at this tempo from bar one, and the
        // client trims its capture to that downbeat, so the grid is known rather than estimated.
        await store.WriteArtifactAsync(
            state.JobId, "beats.json", new BeatGridDto(generated.Bpm, 0.0, 1.0), ct);

        MarkProvided(state, StageNames.Separating, SkippedSeparationExecutor);
        MarkProvided(state, StageNames.ChordDetecting, ProvidedChordsExecutor);

        state.Origin = new TakeOrigin(TakeOrigin.HumTake, Describe(generator.GetProgression(backing.ProgressionId), generated));
    }

    /// <summary>
    /// The sentence the analyzer shows above a hum take. Worded here rather than in the client for
    /// the same reason note colours and chord labels are: naming a mode root is a musical statement,
    /// and the one place allowed to make those is the server.
    ///
    /// <para>Deliberately says what was <em>played to</em> the singer, never what they sang. The
    /// mode the analysis discovers is the melody's own, and stating the backing's mode next to it as
    /// though they were the same fact would pre-empt the answer the page exists to give.</para>
    /// </summary>
    private static string Describe(ChordProgressionDefinition progression, GeneratedMelodyDto generated)
    {
        var modeRootName = PitchNames.Name(generated.TonicPitchClass);
        return $"Hummed over {progression.Name} — {modeRootName} {generated.Mode} at {generated.Bpm:0} BPM";
    }

    /// <summary>
    /// Repeats the loop's chords across the whole take. The user hums over as many passes as they
    /// like, and every pass has to carry the chords that were actually sounding during it — a single
    /// unrepeated loop would leave the rest of the recording with no harmony at all.
    /// </summary>
    public static List<ChordSpan> TileOverTake(
        IReadOnlyList<ChordSpan> loop, double loopSeconds, double takeSeconds)
    {
        var tiled = new List<ChordSpan>();
        for (var pass = 0; pass * loopSeconds < takeSeconds && tiled.Count < MaxSpans; pass++)
        {
            var offset = pass * loopSeconds;
            foreach (var chord in loop)
            {
                var start = offset + chord.StartSec;
                if (start >= takeSeconds)
                {
                    break;
                }
                var end = Math.Min(offset + chord.EndSec, takeSeconds);
                if (end - start > MinSpanSeconds)
                {
                    tiled.Add(chord with { StartSec = start, EndSec = end });
                }
            }
        }
        return tiled;
    }

    /// <summary>
    /// Marks a stage done without running it, and rewrites its plan entry to name what actually
    /// happened. The plan is what the client reports as "who ran" (and what the mock-data banner
    /// reads), so leaving the planned executor in place would be a false claim about the job.
    /// </summary>
    private void MarkProvided(JobState state, string stage, string executor)
    {
        if (state.CompletedStages.Contains(stage))
        {
            return;
        }

        var now = time.GetUtcNow();
        state.StageHistory.Add(new StageRecord(stage, ExecutionTier.Local, executor, now, now));
        state.CompletedStages.Add(stage);

        var index = state.Plan.FindIndex(p => p.Stage == stage);
        if (index >= 0)
        {
            state.Plan[index] = state.Plan[index] with
            {
                Tier = ExecutionTier.Local,
                Executor = executor,
                IsPlaceholder = false,
            };
        }
    }
}
