using Microsoft.ML.OnnxRuntime;
using PoMode.API.Audio;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Separation;

/// <summary>
/// UVR's MDX-Net Voc_FT on the server: the model the browser tier already runs, through the same
/// demix (<see cref="MdxDemix"/>, the C# twin of mdx.js), on the CPU. The faster of the two local
/// separators: a 67 MB vocal model with a spectrogram front end, against HTDemucs's 166 MB waveform
/// model and its 5.7 GB peak. Registered after <see cref="OnnxStemSeparator"/>, so HTDemucs stays the
/// default and this is a pick (Settings, or "Try another model" on a finished song).
/// </summary>
/// <remarks>
/// Only the vocals come out of the model; the instrumental is the mix minus them, as the browser
/// tier's is. UVR asks apps that ship its models to credit it: this is Ultimate Vocal Remover's work
/// (github.com/Anjok07/ultimatevocalremovergui), and the executor picker names it so.
/// </remarks>
public sealed class MdxStemSeparator(ModelRegistry registry, ILogger<MdxStemSeparator> logger) : IStemSeparator
{
    private static readonly TimeSpan SessionKeepAlive = TimeSpan.FromMinutes(10);

    private readonly object _sessionGate = new();
    private InferenceSession? _session;
    private string? _sessionPath;
    private Timer? _evictionTimer;

    public string Name => nameof(MdxStemSeparator);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool UsesLocalModel => true;

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!EnvironmentDetector.IsAzureHosted() && registry.IsDownloaded(ModelCatalog.MdxVocals));

    private InferenceSession GetSession(string modelPath)
    {
        lock (_sessionGate)
        {
            _evictionTimer?.Dispose();
            _evictionTimer = null;
            if (_session is null || _sessionPath != modelPath)
            {
                _session?.Dispose();
                using var options = new Microsoft.ML.OnnxRuntime.SessionOptions
                {
                    IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2),
                };
                _session = new InferenceSession(modelPath, options);
                _sessionPath = modelPath;
            }
            return _session;
        }
    }

    public async Task SeparateAsync(StageContext context, CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.MdxVocals, ct);
        var buffer = AudioConverter.ToStereo44100(AudioDecoder.Decode(context.InputPath));
        var samples = buffer.Samples;
        var frames = samples.Length / 2;
        var left = new float[frames];
        var right = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = samples[i * 2];
            right[i] = samples[(i * 2) + 1];
        }

        var session = GetSession(modelPath);
        var inputName = session.InputMetadata.Keys.Single();
        var shape = new long[] { 1, 4, MdxDemix.DimF, MdxDemix.DimT };
        using var runOptions = new RunOptions();

        float[] RunModel(float[] spec)
        {
            using var input = OrtValue.CreateTensorValueFromMemory(spec, shape);
            using var results = session.Run(runOptions, [inputName], [input], session.OutputNames);
            return results[0].GetTensorDataAsSpan<float>().ToArray();
        }

        var (vocalsL, vocalsR) = await Task.Run(() => MdxDemix.SeparateVocals(left, right, RunModel, progress =>
        {
            context.OnProgress?.Invoke(progress);
        }, ct), ct);

        var vocals = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            vocals[i * 2] = vocalsL[i];
            vocals[(i * 2) + 1] = vocalsR[i];
            samples[i * 2] = Math.Clamp(samples[i * 2] - vocalsL[i], -1f, 1f);
            samples[(i * 2) + 1] = Math.Clamp(samples[(i * 2) + 1] - vocalsR[i], -1f, 1f);
        }
        WavWriter.Write(Path.Combine(context.JobDir, "vocals.wav"), new AudioBuffer(vocals, buffer.SampleRate, 2));
        WavWriter.Write(Path.Combine(context.JobDir, "instrumental.wav"), new AudioBuffer(samples, buffer.SampleRate, 2));
        logger.LogInformation("MDX-Net separated {Seconds:0.0}s of audio for job {JobId}.", frames / (double)MdxDemix.SampleRate, context.JobId);

        lock (_sessionGate)
        {
            _evictionTimer?.Dispose();
            _evictionTimer = new Timer(_ =>
            {
                lock (_sessionGate)
                {
                    _session?.Dispose();
                    _session = null;
                    _sessionPath = null;
                }
            }, null, SessionKeepAlive, Timeout.InfiniteTimeSpan);
        }
    }
}
