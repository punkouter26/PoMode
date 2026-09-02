using PoMode.API.Features.Audio;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Runs the 4-stage pipeline for one job: restart-safe, tier-fallback on failure, cancellable.</summary>
public sealed class AnalysisPipeline(
    JobStore store,
    ExecutionPlanner planner,
    IEnumerable<IStemSeparator> stemSeparators,
    IEnumerable<IPitchTracker> pitchTrackers,
    IEnumerable<IChordRecognizer> chordRecognizers,
    Features.ModalAnalysis.ArtifactModalAnalyzer modalAnalyzer,
    IAnalysisNotifier notifier,
    ILogger<AnalysisPipeline> logger,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Clips shorter than this skip stem separation entirely (snippets, mic checks).</summary>
    private const double ShortClipSeconds = 30.0;

    public async Task RunAsync(string jobId, CancellationToken ct)
    {
        // CancellationToken.None: this is a small local file read, and honoring a cancellation
        // here would throw OperationCanceledException before the try block below can catch it,
        // leaving the job stuck without a terminal state.
        var state = await store.LoadAsync(jobId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");
        if (state.Stage is JobStage.Complete or JobStage.Cancelled)
        {
            // A job cancelled while still queued (DELETE before dequeue) or re-enqueued after
            // completion must not run again.
            return;
        }
        try
        {
            state.Error = null;
            if (state.Plan.Count == 0)
            {
                state.Plan = await planner.PlanAsync(ct);
                await PersistAsync(state, ct);
            }

            // In-stage progress is a UI hint only: pushed over SignalR, never written to disk,
            // throttled to whole-percent steps so a chunk loop cannot flood the hub.
            var lastPublished = -1.0;
            var context = new StageContext(jobId, store.JobDir(jobId), store.InputPath(state), fraction =>
            {
                // Base progress derives from the completed stages; the in-stage fraction rides on
                // top of the DTO only — nothing transient is ever written back to the state.
                var overall = state.Progress + Math.Clamp(fraction, 0, 1) / 4.0;
                if (overall - lastPublished < 0.01)
                {
                    return;
                }
                lastPublished = overall;
                _ = notifier.PublishAsync(state.ToDto(overall), CancellationToken.None);
            });

            await WritePreviewAsync(state, context, ct);

            var backingTask = Task.CompletedTask;

            // Short-clip fast path: real separation costs minutes and gigabytes (local model) or
            // money (cloud) for material where the mix alone analyzes fine. The skip fires only
            // when the planned separator is one of those heavy kinds — placeholders and light
            // test executors still run, so the mock banner and the fallback tests keep their
            // meaning. Every later stage already falls back to the upload when no stems exist.
            var separationPlan = state.Plan.FirstOrDefault(p => p.Stage == StageNames.Separating);
            var heavySeparation = separationPlan is not null
                && (separationPlan.Tier == ExecutionTier.Cloud
                    || stemSeparators.FirstOrDefault(s => s.Name == separationPlan.Executor)?.UsesLocalModel == true);
            if (heavySeparation
                && !state.CompletedStages.Contains(StageNames.Separating)
                && AudioDecoder.TryReadDurationSeconds(store.InputPath(state)) is { } inputSeconds
                && inputSeconds < ShortClipSeconds)
            {
                var now = _time.GetUtcNow();
                state.StageHistory.Add(new StageRecord(
                    StageNames.Separating, ExecutionTier.Local, "SkippedShortClip", now, now));
                state.CompletedStages.Add(StageNames.Separating);
                await PersistAsync(state, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.Separating))
            {
                await EnterStageAsync(state, JobStage.Separating, StageNames.Separating, ct);
                await RunWithFallbackAsync(state, StageNames.Separating, stemSeparators,
                    async (executor, token) => { await executor.SeparateAsync(context, token); return true; }, ct);
                await CompleteStageAsync(state, StageNames.Separating, ct);
                // Stem executors write vocals.wav / instrumental.wav straight into the job
                // folder, bypassing WriteArtifactAsync's blob mirroring — sync them now.
                await store.MirrorToBlobAsync(jobId, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.PitchTracking))
            {
                await EnterStageAsync(state, JobStage.PitchTracking, StageNames.PitchTracking, ct);
                var notes = await RunWithFallbackAsync(state, StageNames.PitchTracking, pitchTrackers,
                    (executor, token) => executor.TrackAsync(context, token), ct);
                await store.WriteArtifactAsync(jobId, "notes.json", notes, ct);
                // Best-effort and independent of the remaining stages, so it runs alongside them
                // instead of serializing the job; awaited before the job is marked complete.
                backingTask = TranscribeBackingAsync(state, context, ct);
                await CompleteStageAsync(state, StageNames.PitchTracking, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.ChordDetecting))
            {
                await EnterStageAsync(state, JobStage.ChordDetecting, StageNames.ChordDetecting, ct);
                var chords = await RunWithFallbackAsync(state, StageNames.ChordDetecting, chordRecognizers,
                    (executor, token) => executor.RecognizeAsync(context, token), ct);
                await store.WriteArtifactAsync(jobId, "chords.json", chords, ct);
                await WriteBeatGridAsync(state, context, ct);
                // The recognizer and the beat grid shared one cached decode — drop the PCM now.
                context.ReleaseAnalysisAudio();
                await CompleteStageAsync(state, StageNames.ChordDetecting, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.ModalAnalysis))
            {
                await EnterStageAsync(state, JobStage.ModalAnalysis, StageNames.ModalAnalysis, ct);
                await modalAnalyzer.AnalyzeAsync(context, ct);
                await CompleteStageAsync(state, StageNames.ModalAnalysis, ct);
            }

            await backingTask; // never throws — it swallows its own failures

            // Stamp the headline facts onto the job record so listings never open result.json.
            if (await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct) is { } finalResult)
            {
                state.TonicName = finalResult.TonicName;
                state.PrimaryMode = finalResult.PrimaryMode?.ToString();
                state.TempoBpm = finalResult.TempoBpm;
            }

            state.Stage = JobStage.Complete;
            await PersistAsync(state, ct);
        }
        catch (OperationCanceledException)
        {
            state.Stage = JobStage.Cancelled;
            await PersistAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed in stage {Stage}.", jobId, state.Stage);
            state.Stage = JobStage.Failed;
            state.Error = ex.Message;
            await PersistAsync(state, CancellationToken.None);
        }
    }

    /// <summary>
    /// Best-effort second transcription: the instrumental stem through any tracker that can
    /// transcribe a file, for the client's "music notes" playback. Deliberately outside the
    /// tier-fallback machinery — when the stem or a capable tracker is missing (Tier 2/cloud
    /// paths), or the run fails, the job simply has no notes-backing.json and the client keeps
    /// that mode silent.
    /// </summary>
    private async Task TranscribeBackingAsync(JobState state, StageContext context, CancellationToken ct)
    {
        try
        {
            var instrumentalPath = Path.Combine(context.JobDir, "instrumental.wav");
            if (!File.Exists(instrumentalPath))
            {
                return;
            }
            // Same tier ordering the planner uses — never DI registration order — and placeholders
            // are excluded outright: fake backing notes would bypass the plan (and its mock banner),
            // and no backing notes is strictly better than fabricated ones. The walk skips
            // unavailable candidates rather than giving up, so a missing model still leaves the
            // classic-DSP fallback (YinPitchTracker) to write real backing notes.
            IFileTranscriber? transcriber = null;
            foreach (var candidate in pitchTrackers.OfType<IFileTranscriber>()
                .Where(t => !t.IsPlaceholder)
                .OrderBy(ExecutionPlanner.EffectiveRank))
            {
                if (await candidate.IsAvailableAsync(ct))
                {
                    transcriber = candidate;
                    break;
                }
            }
            if (transcriber is null)
            {
                return;
            }
            var backingNotes = await transcriber.TranscribeFileAsync(instrumentalPath, ct);
            await store.WriteArtifactAsync(state.JobId, "notes-backing.json", backingNotes, ct);
        }
        catch (OperationCanceledException)
        {
            // The task runs concurrently with later stages, so it must end quietly on cancel —
            // the pipeline's own cancel path owns the job's terminal state.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Backing transcription failed for job {JobId}; continuing without it.", state.JobId);
        }
    }

    /// <summary>
    /// A fast first look, written before the slow stages start: raw-chroma key guess plus a tempo
    /// estimate on the untouched upload. Seconds of work against minutes of pipeline, so the UI
    /// can show "likely G, ~128 BPM" almost immediately. Best-effort; skipped when it already
    /// exists (restart) and never allowed to fail the job.
    /// </summary>
    private async Task WritePreviewAsync(JobState state, StageContext context, CancellationToken ct)
    {
        if (File.Exists(Path.Combine(context.JobDir, "preview.json")))
        {
            return;
        }
        try
        {
            var buffer = context.DecodePreferredAnalysisAudio();
            var chroma = Features.ChordRecognition.ChromaExtractor.Compute(buffer, context.TuningOffsetCents());
            var histogram = new double[12];
            foreach (var frame in chroma.Frames)
            {
                for (var pitchClass = 0; pitchClass < 12; pitchClass++)
                {
                    histogram[pitchClass] += frame[pitchClass];
                }
            }
            var tonic = Features.ModalAnalysis.TonicDetector.DetectFromHistogram(histogram);
            var tempo = TempoEstimator.Estimate(buffer);
            await store.WriteArtifactAsync(state.JobId, "preview.json",
                new AnalysisPreviewDto(
                    Features.ModalAnalysis.PitchNames.Name(tonic.PitchClass),
                    tempo.Bpm,
                    Math.Min(tonic.Confidence, tempo.Confidence)), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Preview estimation failed for job {JobId}; continuing without it.", state.JobId);
        }
        finally
        {
            // The preview decoded the raw upload; drop that PCM before the long stages run.
            context.ReleaseAnalysisAudio();
        }
    }

    /// <summary>
    /// Best-effort beat grid for the client metronome, written alongside chords.json. Prefers the
    /// instrumental stem (same choice ChromaChordRecognizer makes — drums live there); falls back
    /// to the original upload. A failure only costs the metronome, never the job, and a
    /// low-confidence grid is written as-is so the client can gate on it.
    /// </summary>
    private async Task WriteBeatGridAsync(JobState state, StageContext context, CancellationToken ct)
    {
        try
        {
            var audio = context.DecodePreferredAnalysisAudio();
            var grid = TempoEstimator.EstimateGrid(audio);
            await store.WriteArtifactAsync(
                state.JobId, "beats.json", new BeatGridDto(grid.Bpm, grid.FirstBeatSec, grid.Confidence), ct);

            // Written alongside the grid, not instead of it: everything downstream still runs on the
            // single tempo, and this is the honest record of what the tempo actually did.
            var map = TempoEstimator.EstimateTempoMap(audio);
            await store.WriteArtifactAsync(
                state.JobId, "tempo-map.json",
                new TempoMapDto(
                    map.MedianBpm, map.MinBpm, map.MaxBpm, map.IsSteady, map.Confidence,
                    [.. map.Measures.Select(measure => new TempoMeasureDto(
                        measure.Number, measure.StartSec, measure.Bpm, measure.Changed))]),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Beat grid estimation failed for job {JobId}; continuing without it.", state.JobId);
        }
    }

    private async Task<TResult> RunWithFallbackAsync<TExecutor, TResult>(
        JobState state,
        string stage,
        IEnumerable<TExecutor> candidates,
        Func<TExecutor, CancellationToken, Task<TResult>> run,
        CancellationToken ct)
        where TExecutor : IStageExecutor
    {
        var planned = state.Plan.Single(p => p.Stage == stage);
        var ordered = candidates
            .OrderBy(c => c.Name == planned.Executor ? -1 : ExecutionPlanner.EffectiveRank(c))
            .ToList();

        Exception? lastFailure = null;
        foreach (var candidate in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (lastFailure is not null && !await candidate.IsAvailableAsync(ct))
            {
                continue;
            }
            try
            {
                var result = await run(candidate, ct);
                if (candidate.Name != planned.Executor)
                {
                    state.Plan[state.Plan.IndexOf(planned)] = planned with
                    {
                        Tier = candidate.Tier,
                        Executor = candidate.Name,
                        IsPlaceholder = candidate.IsPlaceholder,
                    };
                    await PersistAsync(state, ct);
                    logger.LogWarning("Stage {Stage} fell back from {Planned} to {Actual}.", stage, planned.Executor, candidate.Name);
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                // The input itself is bad (e.g. AudioDecoder's duration cap) — not an executor/tier
                // failure. Falling through to a fake executor would silently fabricate results for a
                // job that should fail outright with the decoder's message.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Executor {Executor} failed for stage {Stage}; trying next tier.", candidate.Name, stage);
                lastFailure = ex;
            }
        }
        throw lastFailure ?? new InvalidOperationException($"No executor ran for stage {stage}.");
    }

    private async Task EnterStageAsync(JobState state, JobStage stage, string stageName, CancellationToken ct)
    {
        state.Stage = stage;
        var planned = state.Plan.FirstOrDefault(p => p.Stage == stageName);
        state.StageHistory.Add(new StageRecord(
            stageName,
            planned?.Tier ?? ExecutionTier.Local,
            planned?.Executor ?? "unplanned",
            _time.GetUtcNow(),
            CompletedAt: null));
        await PersistAsync(state, ct);
    }

    private async Task CompleteStageAsync(JobState state, string stageName, CancellationToken ct)
    {
        state.CompletedStages.Add(stageName);
        // Re-read the plan entry: RunWithFallbackAsync rewrites it when a fallback executor ran,
        // so the history records who actually did the work, not who was scheduled to.
        var recordIndex = state.StageHistory.FindLastIndex(r => r.Stage == stageName);
        if (recordIndex >= 0)
        {
            var planned = state.Plan.FirstOrDefault(p => p.Stage == stageName);
            state.StageHistory[recordIndex] = state.StageHistory[recordIndex] with
            {
                Tier = planned?.Tier ?? state.StageHistory[recordIndex].Tier,
                Executor = planned?.Executor ?? state.StageHistory[recordIndex].Executor,
                CompletedAt = _time.GetUtcNow(),
            };
        }
        await PersistAsync(state, ct);
    }

    private async Task PersistAsync(JobState state, CancellationToken ct)
    {
        await store.SaveAsync(state, ct);
        await notifier.PublishAsync(state.ToDto(), ct);
    }
}
