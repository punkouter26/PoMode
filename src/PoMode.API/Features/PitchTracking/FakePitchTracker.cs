using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>Phase-2 stand-in: deterministic C-major scale regardless of input audio.</summary>
public sealed class FakePitchTracker : IPitchTracker
{
    private static readonly int[] Pitches = [60, 62, 64, 65, 67, 69, 71, 72];

    public string Name => nameof(FakePitchTracker);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool IsPlaceholder => true;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
    {
        IReadOnlyList<NoteEvent> notes = Pitches
            .Select((pitch, i) => new NoteEvent(pitch, i * 0.5, 0.45, 96))
            .ToArray();
        return Task.FromResult(notes);
    }
}
