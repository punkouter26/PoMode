using PoMode.API.Pipeline;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Stage-4 placeholder until the real ModalAnalysisEngine lands in Phase 3.</summary>
public sealed class PlaceholderModalAnalyzer : IModalAnalyzer
{
    public Task AnalyzeAsync(StageContext context, CancellationToken ct)
        => File.WriteAllTextAsync(
            Path.Combine(context.JobDir, "result.json"),
            """{"status":"modal analysis arrives in Phase 3"}""",
            ct);
}
