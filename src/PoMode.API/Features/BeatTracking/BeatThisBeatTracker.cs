using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PoMode.API.Audio;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.BeatTracking;

/// <summary>
/// Neural beat and downbeat tracking with Beat This! (CPJKU, MIT) on the ONNX CPU provider. The
/// classic tracker finds one tempo and a phase by autocorrelating onset energy, which is why it can
/// report a tempo but not where bar one is; this model was trained on annotated beats and downbeats
/// and answers both, so measure numbers can follow the music instead of assuming 4/4 from t=0.
///
/// <para>Every front-end detail follows the reference <c>LogMelSpect</c> exactly, because the export's
/// own card warns that small differences move beats: 22.05 kHz mono (arithmetic-mean downmix), STFT
/// 1024 / hop 441 with a periodic Hann window, reflect-padded and centred, magnitudes scaled by
/// 1/√1024, projected onto the shipped filterbank, then <c>log1p(1000·x)</c>. Chunking reproduces
/// <c>split_piece</c> / <c>aggregate_prediction</c> with <c>keep_first</c>.</para>
/// </summary>
public sealed class BeatThisBeatTracker(ModelRegistry registry, ILogger<BeatThisBeatTracker> logger) : IBeatTracker
{
    private const int SampleRate = 22050;
    private const int FftSize = 1024;
    private const int Hop = 441;
    private const int MelBands = 128;
    private const float LogMultiplier = 1000f;
    private const int ChunkFrames = 1500;
    private const int BorderFrames = 6;
    private const float NoPrediction = -1000f;

    public string Name => nameof(BeatThisBeatTracker);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool UsesLocalModel => true;

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!EnvironmentDetector.IsAzureHosted()
            && registry.IsDownloaded(ModelCatalog.BeatThis)
            && registry.IsDownloaded(ModelCatalog.BeatThisMelFilterbank));

    public async Task<BeatTrackResult> TrackBeatsAsync(StageContext context, CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.BeatThis, ct);
        var filterbankPath = await registry.EnsureAsync(ModelCatalog.BeatThisMelFilterbank, ct);
        var audio = context.DecodePreferredAnalysisAudio();
        return await Task.Run(() => Track(modelPath, filterbankPath, audio, ct), ct);
    }

    private BeatTrackResult Track(string modelPath, string filterbankPath, AudioBuffer audio, CancellationToken ct)
    {
        var samples = AudioDecoder.ResampleBandLimited(AudioDecoder.ToMono(audio), SampleRate).Samples;
        var spect = LogMel(samples, LoadFilterbank(filterbankPath));
        var frameCount = spect.Length / MelBands;

        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        using var session = new InferenceSession(modelPath, options);

        var beatLogits = new float[frameCount];
        var downbeatLogits = new float[frameCount];
        Array.Fill(beatLogits, NoPrediction);
        Array.Fill(downbeatLogits, NoPrediction);

        // split_piece: starts step by chunk - 2·border from -border; the last start is pulled back so
        // the final chunk is full length rather than a short tail.
        var step = ChunkFrames - (2 * BorderFrames);
        var starts = new List<int>();
        for (var start = -BorderFrames; start < frameCount - BorderFrames; start += step)
        {
            starts.Add(start);
        }
        if (frameCount > step)
        {
            starts[^1] = frameCount - (ChunkFrames - BorderFrames);
        }

        // keep_first: later chunks are written first so earlier ones overwrite the overlap.
        for (var s = starts.Count - 1; s >= 0; s--)
        {
            ct.ThrowIfCancellationRequested();
            var start = starts[s];
            var from = Math.Max(start, 0);
            var to = Math.Min(start + ChunkFrames, frameCount);
            var leftPad = Math.Max(0, -start);
            var rightPad = Math.Max(0, Math.Min(BorderFrames, start + ChunkFrames - frameCount));
            var length = leftPad + (to - from) + rightPad;

            var input = new float[length * MelBands];
            Array.Copy(spect, from * MelBands, input, leftPad * MelBands, (to - from) * MelBands);

            using var results = session.Run(
                [NamedOnnxValue.CreateFromTensor("spect", new DenseTensor<float>(input, [1, length, MelBands]))],
                ["beat", "downbeat"]);
            var beat = results[0].AsTensor<float>().ToDenseTensor().Buffer.Span;
            var downbeat = results[1].AsTensor<float>().ToDenseTensor().Buffer.Span;

            // Discard the border frames each side; chunk frame f lands on piece frame start + f.
            for (var f = BorderFrames; f < length - BorderFrames; f++)
            {
                var frame = start + f;
                if (frame >= 0 && frame < frameCount)
                {
                    beatLogits[frame] = beat[f];
                    downbeatLogits[frame] = downbeat[f];
                }
            }
        }

        var (beats, downbeats) = BeatThisDecoder.Decode(beatLogits, downbeatLogits);
        var result = BeatThisDecoder.Summarize(beats, downbeats, Name);
        logger.LogInformation(
            "Beat This!: {Frames} frames, {Beats} beats, {Downbeats} downbeats, {Bpm} BPM (regularity {Confidence}).",
            frameCount, beats.Length, downbeats.Length, result.Grid.Bpm, result.Grid.Confidence);
        return result;
    }

    /// <summary>Log-mel frames, row-major <c>[frames × 128]</c> — the model's input layout.</summary>
    private static float[] LogMel(float[] samples, float[] filterbank)
    {
        var spectrum = MelSpectrogram.StftMagnitudes(samples, FftSize, Hop, scale: 1.0 / Math.Sqrt(FftSize));
        var bins = (FftSize / 2) + 1;
        var spect = new float[spectrum.Length * MelBands];
        Parallel.For(0, spectrum.Length, frame =>
        {
            var magnitudes = spectrum[frame];
            for (var band = 0; band < MelBands; band++)
            {
                var sum = 0f;
                for (var k = 0; k < bins; k++)
                {
                    sum += magnitudes[k] * filterbank[(k * MelBands) + band];
                }
                spect[(frame * MelBands) + band] = MathF.Log(1f + (LogMultiplier * sum));
            }
        });
        return spect;
    }

    /// <summary>Raw little-endian float32, row-major <c>[513 × 128]</c>.</summary>
    private static float[] LoadFilterbank(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var expected = ((FftSize / 2) + 1) * MelBands;
        if (bytes.Length != expected * sizeof(float))
        {
            throw new InvalidOperationException(
                $"Beat This! filterbank at '{path}' is {bytes.Length} bytes; expected {expected * sizeof(float)}.");
        }
        var filterbank = new float[expected];
        Buffer.BlockCopy(bytes, 0, filterbank, 0, bytes.Length);
        return filterbank;
    }
}
