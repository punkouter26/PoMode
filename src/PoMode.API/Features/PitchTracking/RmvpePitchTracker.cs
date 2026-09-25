using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PoMode.API.Audio;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// Vocal-specialised melody tracking with RMVPE, a model trained to follow a singing voice (and
/// only a singing voice) in polyphonic music, on the separated vocal stem. Where Basic Pitch is a
/// general polyphonic transcriber that reports every pitch it hears, RMVPE reports the one sung
/// pitch per 10 ms frame, which is the question the modal analysis actually asks.
///
/// <para>Deliberately not an <see cref="IFileTranscriber"/>: the pipeline also transcribes the
/// instrumental stem for backing notes, and a monophonic voice model is the wrong tool for chords
/// and bass. That job stays with Basic Pitch (or YIN).</para>
/// </summary>
public sealed class RmvpePitchTracker(ModelRegistry registry, ILogger<RmvpePitchTracker> logger) : IPitchTracker
{
    // RMVPE's front end, from RVC's MelSpectrogram(is_half, 128, 16000, 1024, 160, None, 30, 8000).
    private const int SampleRate = 16000;
    private const int FftSize = 1024;
    private const int Hop = 160;
    private const int MelBands = 128;
    private const double MelMinHz = 30;
    private const double MelMaxHz = 8000;
    private const float LogFloor = 1e-5f;

    /// <summary>The U-Net halves the time axis five times, so the frame count must be a multiple of 32.</summary>
    private const int FrameMultiple = 32;

    /// <summary>
    /// Frames per inference call, and context either side. A whole song in one call is what the
    /// reference does, but its memory grows with the song; 20 s chunks with 1.28 s of context
    /// either side give the recurrent layer enough lead-in that the kept centre matches.
    /// </summary>
    private const int ChunkFrames = 2048;
    private const int ContextFrames = 128;

    private static readonly Lazy<float[][]> Filterbank =
        new(() => MelSpectrogram.HtkFilterbank(SampleRate, FftSize, MelBands, MelMinHz, MelMaxHz));

    public string Name => nameof(RmvpePitchTracker);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool UsesLocalModel => true;

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!EnvironmentDetector.IsAzureHosted() && registry.IsDownloaded(ModelCatalog.Rmvpe));

    public async Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
    {
        var vocalsPath = Path.Combine(context.JobDir, "vocals.wav");
        var path = File.Exists(vocalsPath) ? vocalsPath : context.InputPath;
        var offsetCents = context.TuningOffsetCents(logger);
        var modelPath = await registry.EnsureAsync(ModelCatalog.Rmvpe, ct);
        return await Task.Run(() => Transcribe(modelPath, path, offsetCents, ct), ct);
    }

    private IReadOnlyList<NoteEvent> Transcribe(string modelPath, string audioPath, double offsetCents, CancellationToken ct)
    {
        var audio = AudioDecoder.Resample(AudioDecoder.ToMono(AudioDecoder.Decode(audioPath)), SampleRate).Samples;
        var spectrum = MelSpectrogram.StftMagnitudes(audio, FftSize, Hop);
        var frameCount = spectrum.Length;

        // Model layout [1, 128, T]: band-major, so each band's frames are contiguous.
        var filters = Filterbank.Value;
        var mel = new float[MelBands * frameCount];
        var loudness = new double[frameCount];
        Parallel.For(0, frameCount, frame =>
        {
            var magnitudes = spectrum[frame];
            for (var band = 0; band < MelBands; band++)
            {
                mel[(band * frameCount) + frame] =
                    MathF.Log(Math.Max(MelSpectrogram.Project(magnitudes, filters[band]), LogFloor));
            }
            var energy = 0.0;
            foreach (var magnitude in magnitudes)
            {
                energy += (double)magnitude * magnitude;
            }
            loudness[frame] = Math.Sqrt(energy);
        });

        // Per run, not cached: 360 MB of weights is not worth keeping resident between jobs for an
        // executor that only runs when it is the default or picked (same ruling as HTDemucs).
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        using var session = new InferenceSession(modelPath, options);
        var inputName = session.InputMetadata.Keys.Single();
        var outputName = session.OutputMetadata.Keys.Single();
        var salience = new float[frameCount * RmvpeDecoder.Bins];

        for (var keepStart = 0; keepStart < frameCount; keepStart += ChunkFrames)
        {
            ct.ThrowIfCancellationRequested();
            var keepEnd = Math.Min(frameCount, keepStart + ChunkFrames);
            var from = Math.Max(0, keepStart - ContextFrames);
            var to = Math.Min(frameCount, keepEnd + ContextFrames);
            var length = to - from;
            var padded = ((length + FrameMultiple - 1) / FrameMultiple) * FrameMultiple;

            // Zero-padded on the right, as the reference pads the log-mel before inference.
            var input = new float[MelBands * padded];
            for (var band = 0; band < MelBands; band++)
            {
                Array.Copy(mel, (band * frameCount) + from, input, band * padded, length);
            }

            using var results = session.Run(
                [NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(input, [1, MelBands, padded]))],
                [outputName]);
            // [1, padded, 360], row-major: the kept frames are one contiguous run.
            var output = results[0].AsTensor<float>().ToDenseTensor().Buffer.Span;
            output.Slice((keepStart - from) * RmvpeDecoder.Bins, (keepEnd - keepStart) * RmvpeDecoder.Bins)
                .CopyTo(salience.AsSpan(keepStart * RmvpeDecoder.Bins));
        }

        var pitches = RmvpeDecoder.DecodePitch(salience, frameCount);
        var notes = RmvpeDecoder.Segment(pitches, loudness, offsetCents);
        logger.LogInformation(
            "RMVPE: {Frames} frames, {Voiced} voiced, {Notes} notes.",
            frameCount, pitches.Count(p => p is not null), notes.Count);
        return notes;
    }
}
