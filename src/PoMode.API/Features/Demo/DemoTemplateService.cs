using PoMode.API.Features.Analysis;
using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Demo;

/// <summary>
/// Builds the demo template once, through the real pipeline, as an ordinary queued job under a fixed
/// id — so it is restart-safe for free: a build interrupted mid-stage is re-enqueued by
/// <see cref="JobRecoveryService"/> like any other job, and a finished one is found again on the next
/// start and left alone. Users then get copies of it (<see cref="DemoLibrary"/>), never a run of their own.
/// </summary>
public sealed class DemoTemplateService(
    JobStore store,
    AnalysisIntake intake,
    ModalMelodyGenerator generator,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<DemoTemplateService> logger) : BackgroundService
{
    /// <summary>Named in the plan and history in place of the executors that did not run.</summary>
    private const string DemoScoreExecutor = "DemoScore";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!DemoLibrary.IsEnabled(configuration))
        {
            return;
        }
        // Off the startup path: synthesizing half a minute of audio three times over is CPU work the
        // host should not wait on before it starts answering requests.
        await Task.Yield();

        try
        {
            var existing = store.TryFindInputPath(DemoSong.TemplateJobId) is null
                ? null
                : await store.LoadAsync(DemoSong.TemplateJobId, stoppingToken);
            // Finished: ready to copy. Interrupted mid-run: recovery has already re-enqueued it.
            if (existing is not null && existing.Stage is not (JobStage.Failed or JobStage.Cancelled))
            {
                return;
            }

            logger.LogInformation("Building the demo template ({Reason}).",
                existing is null ? "none on disk" : $"the last build ended {existing.Stage}");
            if (Directory.Exists(store.JobDir(DemoSong.TemplateJobId)) && !store.TryDelete(DemoSong.TemplateJobId))
            {
                logger.LogWarning("The old demo template is still in use; leaving it until the next start.");
                return;
            }

            var demo = DemoSong.Compose(generator);
            using var audio = new MemoryStream(demo.Mix);
            await intake.StartAsync(
                fileName: demo.FileName,
                content: audio,
                clientCanInfer: false,
                ct: stoppingToken,
                seed: (job, token) => SeedScoreAsync(job, demo, token),
                ownerId: DemoSong.TemplateOwner,
                jobId: DemoSong.TemplateJobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The demo is a courtesy: failing to build it must never take the host down with it.
            logger.LogError(ex, "Could not build the demo template; new libraries get no demo until the next start.");
        }
    }

    /// <summary>
    /// Hands the job what the server knows because it wrote it, the way a hum take is handed its
    /// backing: the two parts as stems, the chords and the tempo. Estimating them instead measured the
    /// estimators, not the demo — the stem separator (a vocal model) left most of a synthesized flute
    /// out of the "vocals" stem, and a chord recognizer heard chords that were never played (C minor
    /// under a vamp with no E flat in it). Melody transcription and the modal analysis still run for
    /// real on the audio, and the plan names "DemoScore" for the two stages that did not.
    /// The origin is stamped here too, before the job is queued, so every copy inherits the label.
    /// </summary>
    private async Task SeedScoreAsync(JobState job, DemoSong.Composition demo, CancellationToken ct)
    {
        var dir = store.JobDir(job.JobId);
        await File.WriteAllBytesAsync(Path.Combine(dir, "vocals.wav"), demo.Melody, ct);
        await File.WriteAllBytesAsync(Path.Combine(dir, "instrumental.wav"), demo.Backing, ct);
        // Stems written straight into the folder bypass WriteArtifactAsync's mirroring, as a
        // separator's do; the pipeline syncs those after separation, which this job never runs.
        await store.MirrorToBlobAsync(job.JobId, ct);
        await store.WriteArtifactAsync(job.JobId, "chords.json", demo.Chords, ct);
        await store.WriteArtifactAsync(job.JobId, "beats.json", new BeatGridDto(demo.Bpm, 0.0, 1.0), ct);

        var now = time.GetUtcNow();
        job.MarkProvided(StageNames.Separating, DemoScoreExecutor, now);
        job.MarkProvided(StageNames.ChordDetecting, DemoScoreExecutor, now);
        job.Origin = demo.Origin;
    }
}
