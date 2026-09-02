using PoMode.API.Features.Audio;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>
/// The free alternative chord recognizer: the same chroma front end as
/// <see cref="ChromaChordRecognizer"/>, but decoded with <see cref="ChordViterbiDecoder"/> so the
/// chord sequence is globally smoothed instead of per-frame matched. Ranked as a classic fallback
/// so the current per-frame recognizer stays the default; this one runs only when it cannot.
/// </summary>
public sealed class ViterbiChordRecognizer : IChordRecognizer
{
    public string Name => nameof(ViterbiChordRecognizer);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool IsClassicFallback => true;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct)
    {
        var buffer = context.DecodePreferredAnalysisAudio();
        var chromaGram = ChromaExtractor.Compute(buffer, context.TuningOffsetCents());
        var frames = ChordViterbiDecoder.Decode(chromaGram.Frames);

        // Same beat-grid snapping as the default recognizer; the Viterbi path is already smooth,
        // so the segmenter's median window has little left to do and is kept at its default only
        // for consistency between the two recognizers' outputs.
        var grid = TempoEstimator.EstimateGrid(buffer);
        IReadOnlyList<ChordSpan> spans = ChordSegmenter.Segment(frames, chromaGram.FramesPerSecond, grid);
        return Task.FromResult(spans);
    }
}
