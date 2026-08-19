using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.StemSeparation;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;

namespace PoMode.Integration;

public sealed class AnalysisPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-pipe-{Guid.NewGuid():N}");
    private readonly JobStore _store;
    private readonly RecordingNotifier _notifier = new();

    public AnalysisPipelineTests()
    {
        _store = new JobStore(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
            TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingNotifier : IAnalysisNotifier
    {
        public List<JobStatusDto> Published { get; } = [];
        public Task PublishAsync(JobStatusDto status, CancellationToken ct)
        {
            lock (Published) Published.Add(status);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingStemSeparator : IStemSeparator
    {
        private readonly FakeStemSeparator _inner = new();
        public int Calls;
        public string Name => nameof(CountingStemSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task SeparateAsync(StageContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return _inner.SeparateAsync(context, ct);
        }
    }

    private sealed class ThrowingStemSeparator : IStemSeparator
    {
        public string Name => nameof(ThrowingStemSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task SeparateAsync(StageContext context, CancellationToken ct)
            => throw new InvalidOperationException("simulated OOM");
    }

    private sealed class InvalidDataStemSeparator : IStemSeparator
    {
        private readonly string _message;
        public InvalidDataStemSeparator(string message) => _message = message;
        public string Name => nameof(InvalidDataStemSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task SeparateAsync(StageContext context, CancellationToken ct)
            => throw new InvalidDataException(_message);
    }

    private sealed class HangingStemSeparator : IStemSeparator
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => nameof(HangingStemSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public async Task SeparateAsync(StageContext context, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private AnalysisPipeline Pipeline(IStemSeparator[]? separators = null)
    {
        separators ??= [new FakeStemSeparator()];
        IPitchTracker[] trackers = [new FakePitchTracker()];
        IChordRecognizer[] chords = [new FakeChordRecognizer()];
        return new AnalysisPipeline(
            _store,
            new ExecutionPlanner(separators, trackers, chords),
            separators, trackers, chords,
            new ArtifactModalAnalyzer(_store, NullLogger<ArtifactModalAnalyzer>.Instance),
            _notifier,
            NullLogger<AnalysisPipeline>.Instance);
    }

    private async Task<JobState> NewJobAsync()
    {
        using var content = new MemoryStream(TestAudio.MakeWav());
        return await _store.CreateAsync("song.wav", content, CancellationToken.None);
    }

    [Fact]
    public async Task Full_run_produces_all_artifacts_and_completes()
    {
        var job = await NewJobAsync();

        await Pipeline().RunAsync(job.JobId, CancellationToken.None);

        var dir = _store.JobDir(job.JobId);
        foreach (var artifact in new[] { "vocals.wav", "instrumental.wav", "notes.json", "chords.json", "result.json" })
        {
            Assert.True(File.Exists(Path.Combine(dir, artifact)), $"missing {artifact}");
        }
        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
        Assert.Equal(1.0, final.Progress);
        Assert.Equal(4, final.CompletedStages.Count);
        Assert.Equal(JobStage.Complete, _notifier.Published[^1].Stage);
    }

    [Fact]
    public async Task Rerun_after_completion_skips_all_stages()
    {
        var job = await NewJobAsync();
        var counter = new CountingStemSeparator();
        var pipeline = Pipeline([counter]);

        await pipeline.RunAsync(job.JobId, CancellationToken.None);
        await pipeline.RunAsync(job.JobId, CancellationToken.None); // simulated restart re-enqueue

        Assert.Equal(1, counter.Calls);
        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
    }

    [Fact]
    public async Task Executor_failure_falls_through_to_next_candidate_and_updates_plan()
    {
        var job = await NewJobAsync();
        // Throwing executor is planned first (registration order breaks the tier tie).
        var pipeline = Pipeline([new ThrowingStemSeparator(), new FakeStemSeparator()]);

        await pipeline.RunAsync(job.JobId, CancellationToken.None);

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
        Assert.Equal(nameof(FakeStemSeparator), final.Plan.Single(p => p.Stage == StageNames.Separating).Executor);
    }

    [Fact]
    public async Task All_candidates_failing_marks_job_failed_with_error()
    {
        var job = await NewJobAsync();
        var pipeline = Pipeline([new ThrowingStemSeparator()]);

        await pipeline.RunAsync(job.JobId, CancellationToken.None);

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Failed, final!.Stage);
        Assert.Contains("simulated OOM", final.Error);
    }

    [Fact]
    public async Task Cancellation_marks_job_cancelled()
    {
        var job = await NewJobAsync();
        var hanging = new HangingStemSeparator();
        var pipeline = Pipeline([hanging]);
        using var cts = new CancellationTokenSource();

        var run = pipeline.RunAsync(job.JobId, cts.Token);
        await hanging.Started.Task;
        cts.Cancel();
        await run;

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Cancelled, final!.Stage);
    }

    [Fact]
    public async Task InvalidDataException_from_an_executor_fails_the_job_without_falling_through()
    {
        var job = await NewJobAsync();
        const string message = "Audio is 999 s long; the limit is 900 s.";
        var counter = new CountingStemSeparator();
        var pipeline = Pipeline([new InvalidDataStemSeparator(message), counter]);

        await pipeline.RunAsync(job.JobId, CancellationToken.None);

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Failed, final!.Stage);
        Assert.Equal(message, final.Error);
        Assert.Equal(0, counter.Calls);
    }
}
