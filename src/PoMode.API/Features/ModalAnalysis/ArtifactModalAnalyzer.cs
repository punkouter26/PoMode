using PoMode.API.Features.Analysis;
using PoMode.API.Features.Audio;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Stage 4: reads notes.json + chords.json, runs the deterministic engine, writes result.json.</summary>
public sealed class ArtifactModalAnalyzer(JobStore store, ILogger<ArtifactModalAnalyzer> logger)
{
    /// <summary>Writes result.json into <see cref="StageContext.JobDir"/>. Deliberately not behind
    /// a seam: the modal stage has one implementation and no tier fallback, so an interface here
    /// was indirection with no consumer.</summary>
    public async Task AnalyzeAsync(StageContext context, CancellationToken ct)
    {
        var notes = await store.ReadArtifactListAsync<NoteEvent>(context.JobId, "notes.json", ct);
        var chords = await store.ReadArtifactListAsync<ChordSpan>(context.JobId, "chords.json", ct);

        var (bpm, estimated) = await EstimateTempoAsync(context, ct);

        var result = ModalAnalysisEngine.Analyze(
            notes, chords, bpm, estimated, context.TuningOffsetCents(logger));

        await store.WriteArtifactAsync(context.JobId, "result.json", result, ct);
    }

    /// <summary>
    /// The chord stage already estimated the tempo on the preferred audio and persisted it as
    /// beats.json — reuse that instead of re-decoding the whole track. Only when the artifact is
    /// missing (its best-effort write failed) does this fall back to estimating directly. A
    /// failure must never fail the whole job — the tempo stays at the Phase-3 default and is
    /// still labelled estimated.
    /// </summary>
    private async Task<(double Bpm, bool Estimated)> EstimateTempoAsync(StageContext context, CancellationToken ct)
    {
        try
        {
            if (await store.ReadArtifactAsync<BeatGridDto>(context.JobId, "beats.json", ct) is { } grid)
            {
                return grid.Confidence > 0 ? (grid.Bpm, false) : (120.0, true);
            }

            var buffer = AudioDecoder.Decode(context.PreferredAnalysisPath);
            var estimate = TempoEstimator.Estimate(buffer);

            return estimate.Confidence > 0 ? (estimate.Bpm, false) : (120.0, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tempo estimation failed for job {JobId}; falling back to the 120 BPM default.", context.JobId);
            return (120.0, true);
        }
    }
}
