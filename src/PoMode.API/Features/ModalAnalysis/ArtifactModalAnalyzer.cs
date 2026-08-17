using PoMode.API.Features.Analysis;
using PoMode.API.Features.Audio;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Stage 4: reads notes.json + chords.json, runs the deterministic engine, writes result.json.</summary>
public sealed class ArtifactModalAnalyzer(JobStore store, ILogger<ArtifactModalAnalyzer> logger) : IModalAnalyzer
{
    public async Task AnalyzeAsync(StageContext context, CancellationToken ct)
    {
        var notes = await store.ReadArtifactListAsync<NoteEvent>(context.JobId, "notes.json", ct);
        var chords = await store.ReadArtifactListAsync<ChordSpan>(context.JobId, "chords.json", ct);

        var (bpm, estimated) = EstimateTempo(context);

        var result = ModalAnalysisEngine.Analyze(notes, chords, bpm, estimated);

        await store.WriteArtifactAsync(context.JobId, "result.json", result, ct);
    }

    /// <summary>
    /// Prefers the separated instrumental stem over the raw input so vocal noise doesn't confuse
    /// the beat tracker. A missing or undecodable file must never fail the whole job — it just
    /// means the tempo stays at the Phase-3 default and is still labelled estimated.
    /// </summary>
    private (double Bpm, bool Estimated) EstimateTempo(StageContext context)
    {
        try
        {
            var instrumentalPath = Path.Combine(context.JobDir, "instrumental.wav");
            var audioPath = File.Exists(instrumentalPath) ? instrumentalPath : context.InputPath;

            var buffer = AudioDecoder.Decode(audioPath);
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
