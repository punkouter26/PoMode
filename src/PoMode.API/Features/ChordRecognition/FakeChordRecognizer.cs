using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>Phase-2 stand-in: a fixed C / Am / F / G progression, two seconds per chord.</summary>
public sealed class FakeChordRecognizer : IChordRecognizer
{
    private static readonly (string Symbol, string Root, string Quality)[] Progression =
        [("C", "C", "maj"), ("Am", "A", "min"), ("F", "F", "maj"), ("G", "G", "maj")];

    public string Name => nameof(FakeChordRecognizer);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool IsPlaceholder => true;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct)
    {
        IReadOnlyList<ChordSpan> chords = Progression
            .Select((chord, i) => new ChordSpan(chord.Symbol, chord.Root, chord.Quality, i * 2.0, (i + 1) * 2.0))
            .ToArray();
        return Task.FromResult(chords);
    }
}
