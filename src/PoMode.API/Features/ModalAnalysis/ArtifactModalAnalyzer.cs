using PoMode.API.Features.Analysis;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Stage 4: reads notes.json + chords.json, runs the deterministic engine, writes result.json.</summary>
public sealed class ArtifactModalAnalyzer(JobStore store) : IModalAnalyzer
{
    public async Task AnalyzeAsync(StageContext context, CancellationToken ct)
    {
        var notes = await store.ReadArtifactListAsync<NoteEvent>(context.JobId, "notes.json", ct);
        var chords = await store.ReadArtifactListAsync<ChordSpan>(context.JobId, "chords.json", ct);

        var result = ModalAnalysisEngine.Analyze(notes, chords);

        await store.WriteArtifactAsync(context.JobId, "result.json", result, ct);
    }
}
