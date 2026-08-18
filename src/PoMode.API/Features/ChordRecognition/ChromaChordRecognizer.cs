using PoMode.API.Features.Audio;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>
/// Real chord recognition: chains <see cref="ChromaExtractor"/> (Task 1), <see cref="ChordMatcher"/>
/// (Task 2) and <see cref="ChordSegmenter"/> (Task 3) behind the <see cref="IChordRecognizer"/> seam.
/// Pure DSP — no model to download, no network call — so unlike the Onnx-backed stages this is
/// unconditionally available, including when hosted in Azure.
/// </summary>
public sealed class ChromaChordRecognizer : IChordRecognizer
{
    public string Name => nameof(ChromaChordRecognizer);
    public ExecutionTier Tier => ExecutionTier.Local;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct)
    {
        var buffer = context.DecodePreferredAnalysisAudio();
        var chromaGram = ChromaExtractor.Compute(buffer);

        var frames = chromaGram.Frames
            .Select(chroma => ChordMatcher.Match(chroma))
            .ToArray();

        // §13.6 fix (b): chord boundaries snap to the beat grid when the tempo estimate is
        // confident; Segment falls back to the duration-floor path when it is not (sustained
        // pads, silence — exactly the material with no beats to snap to).
        var grid = TempoEstimator.EstimateGrid(buffer);
        IReadOnlyList<ChordSpan> spans = ChordSegmenter.Segment(frames, chromaGram.FramesPerSecond, grid);
        return Task.FromResult(spans);
    }
}
