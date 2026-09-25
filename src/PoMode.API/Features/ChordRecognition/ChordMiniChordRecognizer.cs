using System.Buffers.Binary;
using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PoMode.API.Audio;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>
/// Chord recognition with a trained model: ChordMini's ChordNet (a ~2.3 M-parameter transformer,
/// MIT), from <see cref="ModelCatalog.ChordMini"/>. The DSP recognizers match 24 triad templates to
/// chroma; this one was trained on real recordings over 170 chord classes, so it can tell a chord
/// from the melody note ringing over it.
///
/// <para>The graph is the classifier only. Its features are librosa 0.11's recursive constant-Q
/// transform, and the model card is blunt that a different CQT costs accuracy even at 0.998
/// correlation — so the transform here runs the plan the model ships with
/// (<see cref="ModelCatalog.ChordMiniCqtPlan"/>: the half-band resampling filter, and each octave's
/// sparse FFT basis, baked from librosa), step for step as musetric's reference host runs it on the
/// GPU: halve the rate, frame 512 samples centred on each hop, FFT, project, <c>log(|x| + 1e-6)</c>.
/// </para>
///
/// <para>The 170 labels fold into the vocabulary everything downstream voices and reasons about:
/// triads and the three sevenths keep their quality; sixths, sus and the diminished sevenths fall to
/// their nearest triad, keeping the root. Spans are cut by <see cref="ChordSegmenter"/>, on the
/// beats the beat tracker heard when there are any, like every recognizer.</para>
/// </summary>
public sealed class ChordMiniChordRecognizer(ModelRegistry registry, ILogger<ChordMiniChordRecognizer> logger)
    : IChordRecognizer
{
    private const int SampleRate = 22050;
    private const int WindowFrames = 108;
    private const int WindowsPerRun = 16;
    private const int Bins = 144;
    private const int Classes = 170;
    private const int SmoothingFrames = 9;

    /// <summary>What a padded frame holds: the feature of silence, rather than a zero that reads as loud.</summary>
    private static readonly float Silence = MathF.Log(1e-6f);

    /// <summary>The model card's vocabulary, index for index: 14 qualities per root, C to B, then X and N.</summary>
    private static readonly string[] Qualities =
        ["min", "", "dim", "aug", "min6", "maj6", "min7", "minmaj7", "maj7", "7", "dim7", "hdim7", "sus2", "sus4"];

    public string Name => nameof(ChordMiniChordRecognizer);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool UsesLocalModel => true;

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!EnvironmentDetector.IsAzureHosted()
            && registry.IsDownloaded(ModelCatalog.ChordMini)
            && registry.IsDownloaded(ModelCatalog.ChordMiniCqtPlan));

    public async Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.ChordMini, ct);
        var planPath = await registry.EnsureAsync(ModelCatalog.ChordMiniCqtPlan, ct);
        var audio = context.DecodePreferredAnalysisAudio();
        return await Task.Run(() => Recognize(modelPath, planPath, audio, context.Beats, ct), ct);
    }

    private IReadOnlyList<ChordSpan> Recognize(
        string modelPath, string planPath, AudioBuffer audio, IReadOnlyList<double>? beats, CancellationToken ct)
    {
        var mono = AudioDecoder.ResampleBandLimited(AudioDecoder.ToMono(audio), SampleRate).Samples;
        var plan = CqtPlan.Load(File.ReadAllBytes(planPath));
        var features = plan.LogMagnitude(mono);
        var frameCount = features.Length / Bins;
        var logits = Classify(modelPath, features, frameCount, ct);

        var frames = new (ChordCandidate Chord, double Score)[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var (best, score) = SmoothedArgMax(logits, frame, frameCount);
            frames[frame] = (Candidate(best), score);
        }

        logger.LogInformation("ChordMini: {Frames} frames, {Distinct} distinct chords.",
            frameCount, frames.Select(frame => frame.Chord).Distinct().Count());
        // ChordNet's own 9-frame smoothing has already run, so the segmenter's median adds nothing.
        return ChordSegmenter.Segment(frames, plan.FramesPerSecond,
            beats is null ? TempoEstimator.EstimateGrid(audio) : null, medianWindow: 1, beats: beats);
    }

    /// <summary>Logits [frames, 170]. The graph is static at 16 windows of 108 frames, so the track
    /// goes in padded groups of 16; windows never interact, so padding changes no real logit.</summary>
    private static float[] Classify(string modelPath, float[] features, int frameCount, CancellationToken ct)
    {
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
        using var session = new InferenceSession(modelPath, options);

        var windows = (frameCount + WindowFrames - 1) / WindowFrames;
        var logits = new float[frameCount * Classes];
        var runFrames = WindowsPerRun * WindowFrames;
        for (var first = 0; first < windows; first += WindowsPerRun)
        {
            ct.ThrowIfCancellationRequested();
            var input = new float[runFrames * Bins];
            Array.Fill(input, Silence);
            var start = first * WindowFrames;
            var count = Math.Min(runFrames, frameCount - start);
            Array.Copy(features, start * Bins, input, 0, count * Bins);

            using var results = session.Run(
                [NamedOnnxValue.CreateFromTensor("features", new DenseTensor<float>(input, [WindowsPerRun, WindowFrames, Bins]))]);
            results[0].AsTensor<float>().ToDenseTensor().Buffer.Span[..(count * Classes)]
                .CopyTo(logits.AsSpan(start * Classes));
        }
        return logits;
    }

    /// <summary>The class with the highest logit averaged over the 9 frames centred here (fewer at the
    /// edges), and its margin over the runner-up as the score.</summary>
    private static (int Class, double Score) SmoothedArgMax(float[] logits, int frame, int frameCount)
    {
        var from = Math.Max(0, frame - (SmoothingFrames / 2));
        var to = Math.Min(frameCount, frame + (SmoothingFrames / 2) + 1);
        var best = 0;
        var bestValue = double.NegativeInfinity;
        var second = double.NegativeInfinity;
        for (var label = 0; label < Classes; label++)
        {
            var sum = 0.0;
            for (var at = from; at < to; at++)
            {
                sum += logits[(at * Classes) + label];
            }
            var mean = sum / (to - from);
            if (mean > bestValue)
            {
                (second, bestValue, best) = (bestValue, mean, label);
            }
            else if (mean > second)
            {
                second = mean;
            }
        }
        return (best, bestValue - second);
    }

    /// <summary>A class index as the vocabulary the rest of the app voices: see the class remarks.</summary>
    private static ChordCandidate Candidate(int label)
    {
        if (label >= 12 * Qualities.Length)
        {
            return ChordTemplates.NoChord; // X (unknown) and N (no chord) alike: nothing to voice
        }

        var root = label / Qualities.Length;
        var (quality, suffix) = Qualities[label % Qualities.Length] switch
        {
            "min" or "min6" or "minmaj7" => ("min", "m"),
            "min7" => ("min7", "m7"),
            "maj7" => ("maj7", "maj7"),
            "7" => ("7", "7"),
            "dim" or "dim7" or "hdim7" => ("dim", "dim"),
            "aug" => ("aug", "aug"),
            // Major, maj6, and sus: a sus chord has no third to name, and the app's analysis reads
            // a chord without "min" as major — the closest of the qualities it can voice.
            _ => ("maj", ""),
        };
        var name = PitchNames.Name(root);
        return new ChordCandidate(name + suffix, name, quality, root);
    }

    /// <summary>
    /// librosa's recursive constant-Q transform, as a baked plan: the format is musetric's
    /// <c>cqt-plan.bin</c> (<c>packages/cqt/src/cqt/planDecode.es.ts</c>), read field for field.
    /// </summary>
    private sealed class CqtPlan
    {
        private const uint Magic = 0x5451434d;
        private const int FftSize = 512;

        private int _hop;
        private int _bins;
        private int _earlyDownsamples;
        private (int Index, int Hop, int BinStart, int BinCount)[] _octaves = [];
        private uint[] _rowOffsets = [];
        private uint[] _fftBins = [];
        private float[] _coefficients = [];
        private float[] _halfTaps = [];
        private int _tapCount;
        private double _sampleRate;

        public double FramesPerSecond => _sampleRate / _hop;

        public static CqtPlan Load(byte[] bytes)
        {
            var span = bytes.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic || BinaryPrimitives.ReadUInt32LittleEndian(span[8..]) != 128)
            {
                throw new InvalidOperationException("Not a CQT plan (bad magic or header size).");
            }

            var octaveCount = (int)U32(span, 48);
            var tapCount = (int)U32(span, 52);
            var coefficientCount = (int)U32(span, 56);
            var bins = (int)U32(span, 28);
            var octavesOffset = (int)U32(span, 72);
            var plan = new CqtPlan
            {
                _sampleRate = BinaryPrimitives.ReadDoubleLittleEndian(span[16..]),
                _hop = (int)U32(span, 24),
                _bins = bins,
                _earlyDownsamples = (int)U32(span, 36),
                _tapCount = tapCount,
                _octaves = new (int, int, int, int)[octaveCount],
                _rowOffsets = ReadArray(span, (int)U32(span, 76), bins + 1, BinaryPrimitives.ReadUInt32LittleEndian),
                _fftBins = ReadArray(span, (int)U32(span, 80), coefficientCount, BinaryPrimitives.ReadUInt32LittleEndian),
                _coefficients = ReadArray(span, (int)U32(span, 84), coefficientCount * 2, BinaryPrimitives.ReadSingleLittleEndian),
                _halfTaps = ReadArray(span, (int)U32(span, 92), (tapCount + 1) / 2, BinaryPrimitives.ReadSingleLittleEndian),
            };
            for (var index = 0; index < octaveCount; index++)
            {
                var at = octavesOffset + (index * 32);
                if (U32(span, at + 16) != FftSize)
                {
                    throw new InvalidOperationException("Only 512-point CQT plans are supported.");
                }
                plan._octaves[index] = ((int)U32(span, at), (int)U32(span, at + 12), (int)U32(span, at + 20), (int)U32(span, at + 24));
            }
            return plan;
        }

        /// <summary>Features <c>[frames, bins]</c>, row-major, from mono PCM at the plan's rate.</summary>
        public float[] LogMagnitude(float[] samples)
        {
            // Level l is the signal halved l + 1 times; octave o reads level (early + o - 1).
            var levels = new List<float[]>();
            var level = samples;
            for (var index = 0; index < _earlyDownsamples + _octaves.Length - 1; index++)
            {
                levels.Add(level = Halve(level));
            }

            var frameCount = _octaves.Min(octave =>
                1 + (levels[_earlyDownsamples + octave.Index - 1].Length / octave.Hop));
            var features = new float[frameCount * _bins];
            var spectrum = new Complex[FftSize];
            foreach (var octave in _octaves)
            {
                var input = levels[_earlyDownsamples + octave.Index - 1];
                for (var frame = 0; frame < frameCount; frame++)
                {
                    // Centred frames with zero padding: librosa's stft(center=True, pad_mode='constant').
                    var offset = (frame * octave.Hop) - (FftSize / 2);
                    for (var sample = 0; sample < FftSize; sample++)
                    {
                        var at = offset + sample;
                        spectrum[sample] = at >= 0 && at < input.Length ? input[at] : 0.0;
                    }
                    Fft.Transform(spectrum);

                    for (var bin = octave.BinStart; bin < octave.BinStart + octave.BinCount; bin++)
                    {
                        var sum = Complex.Zero;
                        for (var k = _rowOffsets[bin]; k < _rowOffsets[bin + 1]; k++)
                        {
                            sum += new Complex(_coefficients[2 * k], _coefficients[(2 * k) + 1]) * spectrum[_fftBins[k]];
                        }
                        features[(frame * _bins) + bin] = (float)Math.Log(sum.Magnitude + 1e-6);
                    }
                }
            }
            return features;
        }

        /// <summary>The plan's symmetric half-band low-pass, then every other sample, scaled by √2 as
        /// librosa scales each octave down; zeros beyond both ends.</summary>
        private float[] Halve(float[] input)
        {
            var delay = (_tapCount - 1) / 2;
            var output = new float[(input.Length + 1) / 2];
            for (var index = 0; index < output.Length; index++)
            {
                var center = index * 2;
                var value = 0.0;
                for (var tap = 0; tap < _tapCount; tap++)
                {
                    var source = center + tap - delay;
                    if (source >= 0 && source < input.Length)
                    {
                        value += input[source] * _halfTaps[Math.Abs(tap - delay)];
                    }
                }
                output[index] = (float)(value * Math.Sqrt(2));
            }
            return output;
        }

        private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);

        private delegate T Reader<out T>(ReadOnlySpan<byte> source);

        private static T[] ReadArray<T>(ReadOnlySpan<byte> span, int offset, int count, Reader<T> read)
        {
            var values = new T[count];
            for (var index = 0; index < count; index++)
            {
                values[index] = read(span[(offset + (index * 4))..]);
            }
            return values;
        }
    }
}
