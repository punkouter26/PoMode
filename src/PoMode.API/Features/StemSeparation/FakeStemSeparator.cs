using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.StemSeparation;

/// <summary>Phase-2 stand-in: copies the input as both stems so downstream stages have real files.</summary>
public sealed class FakeStemSeparator : IStemSeparator
{
    public string Name => nameof(FakeStemSeparator);
    public ExecutionTier Tier => ExecutionTier.Local;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task SeparateAsync(StageContext context, CancellationToken ct)
    {
        File.Copy(context.InputPath, Path.Combine(context.JobDir, "vocals.wav"), overwrite: true);
        File.Copy(context.InputPath, Path.Combine(context.JobDir, "instrumental.wav"), overwrite: true);
        return Task.CompletedTask;
    }
}
