using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// "Try another model" for one stage of a finished analysis: a new job beside the original, holding
/// everything the stages before the chosen one produced, with the chosen stage pinned to the chosen
/// executor and every stage after it run again on what that one finds. The original is untouched, so
/// the two are an ordinary pair of runs of one song in the library, ready to be ticked and compared.
/// </summary>
/// <remarks>
/// It needs no pipeline support: a job whose earlier stages are complete with their artifacts on disk
/// is exactly the shape the pipeline restarts from after a crash.
/// </remarks>
public sealed class JobRerun(JobStore store, ExecutionPlanner planner, JobQueue queue)
{
    /// <summary>Each stage's own artifacts. Anything not listed for a stage that is kept (later
    /// stages' output, and caches derived from it) is removed from the new job.</summary>
    private static readonly (string Stage, string[] Files)[] Produces =
    [
        (StageNames.Separating, ["vocals.wav", "instrumental.wav"]),
        (StageNames.PitchTracking, ["notes.json", "notes-backing.json"]),
        (StageNames.ChordDetecting, ["chords.json", "beats.json", "tempo-map.json"]),
        (StageNames.ModalAnalysis, ["result.json"]),
    ];

    public static IReadOnlyList<string> Stages { get; } = [.. Produces.Select(p => p.Stage)];

    /// <summary>The new job, or null when the source is not the caller's finished analysis or the
    /// stage is not one of the four.</summary>
    public async Task<JobState?> StartAsync(string sourceJobId, string ownerId, string stage, string executor, CancellationToken ct)
    {
        var stageIndex = Array.FindIndex(Produces, p => p.Stage == stage);
        if (stageIndex < 0
            || await store.LoadAsync(sourceJobId, ct) is not { Stage: JobStage.Complete } source
            || source.OwnerId != ownerId
            // A sung take, a beat or the demo had stages handed to it rather than run (the backing's
            // chords, the score), and re-running a later stage would rediscover them from audio that
            // cannot carry them. Only an ordinary upload is re-run.
            || source.Origin is not null)
        {
            return null;
        }

        var kept = Produces.Take(stageIndex).Select(p => p.Stage).ToHashSet();
        var keptFiles = Produces.Take(stageIndex).SelectMany(p => p.Files)
            .Append(Path.GetFileName(store.InputPath(source)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copy = await store.CopyAsync(source, ownerId, ct, keptFiles.Contains);

        var plan = await planner.PlanAsync(browserCanInfer: false, new Dictionary<string, string> { [stage] = executor }, ct);
        // The kept stages keep the plan entries that describe who really produced their artifacts.
        copy.Plan = [.. plan.Select(entry => kept.Contains(entry.Stage)
            ? source.Plan.FirstOrDefault(p => p.Stage == entry.Stage) ?? entry
            : entry)];
        copy.CompletedStages = [.. source.CompletedStages.Where(kept.Contains)];
        copy.StageHistory = [.. source.StageHistory.Where(record => kept.Contains(record.Stage))];
        copy.Stage = JobStage.Uploaded;
        copy.Error = null;
        copy.TonicName = null;
        copy.PrimaryMode = null;
        copy.TempoBpm = null;
        await store.SaveAsync(copy, ct);
        await queue.EnqueueAsync(copy.JobId, ct);
        return copy;
    }
}
