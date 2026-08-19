using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.StemSeparation;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// Tier 2 inside the real pipeline: a browser that answers finishes the job with its own notes, and a
/// browser that never answers must not be able to wedge it.
/// </summary>
public sealed class ClientDelegatedFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-tier2-{Guid.NewGuid():N}");
    private readonly JobStore _store;
    private readonly FakeTimeProvider _time = new();
    private readonly ClientWorkRegistry _registry;
    private readonly RecordingNotifier _notifier = new();

    public ClientDelegatedFallbackTests()
    {
        _store = new JobStore(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
            TimeProvider.System);
        _registry = new ClientWorkRegistry(_time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingNotifier : IAnalysisNotifier
    {
        public List<JobStage> Stages { get; } = [];
        public Task PublishAsync(JobStatusDto status, CancellationToken ct)
        {
            lock (Stages) Stages.Add(status.Stage);
            return Task.CompletedTask;
        }
        public bool Saw(JobStage stage)
        {
            lock (Stages) return Stages.Contains(stage);
        }
    }

    /// <summary>
    /// A paid-tier stand-in. The fall-through target has to be BELOW ClientDelegated: a Local tracker
    /// would outrank the browser and the stage would never park at all — which is exactly right, and is
    /// what an earlier version of these tests got wrong.
    /// </summary>
    private sealed class StubCloudPitchTracker : IPitchTracker
    {
        public string Name => nameof(StubCloudPitchTracker);
        public ExecutionTier Tier => ExecutionTier.Cloud;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<NoteEvent>>([new NoteEvent(48, 0.0, 1.0, 70)]);
    }

    private ClientDelegatedPitchTracker Tracker(int timeoutSeconds = 300)
        => new(_registry, _store, _notifier,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tier2:TimeoutSeconds"] = timeoutSeconds.ToString(),
            }).Build(),
            NullLogger<ClientDelegatedPitchTracker>.Instance);

    private AnalysisPipeline Pipeline(IPitchTracker[] trackers)
    {
        IStemSeparator[] separators = [new FakeStemSeparator()];
        IChordRecognizer[] chords = [new ChromaChordRecognizer()];
        return new AnalysisPipeline(
            _store,
            new ExecutionPlanner(separators, trackers, chords),
            separators, trackers, chords,
            new ArtifactModalAnalyzer(_store, NullLogger<ArtifactModalAnalyzer>.Instance),
            _notifier,
            NullLogger<AnalysisPipeline>.Instance);
    }


    /// <summary>
    /// Waits for the stage to park. Fails fast with the pipeline's own exception if the run finishes
    /// first — otherwise a pipeline that threw before parking would spin this loop forever.
    /// </summary>
    private async Task WaitForParkAsync(Task run, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!_registry.IsWaiting(jobId))
        {
            if (run.IsCompleted)
            {
                await run; // surfaces the real failure
                var state = await _store.LoadAsync(jobId, CancellationToken.None);
                throw new InvalidOperationException(
                    $"The run finished without parking. Stage={state?.Stage}, Error={state?.Error}");
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The stage never parked within 20 s.");
            }
            await Task.Delay(10);
        }
    }

    private async Task<JobState> NewJobAsync(IPitchTracker[] trackers)
    {
        using var content = new MemoryStream(TestAudio.MakeWav());
        var state = await _store.CreateAsync("song.wav", content, CancellationToken.None);
        // Plan with the browser declared capable, which is what an upload from a WebGPU/WASM-capable
        // client does.
        state.Plan = await new ExecutionPlanner([new FakeStemSeparator()], trackers, [new ChromaChordRecognizer()])
            .PlanAsync(browserCanInfer: true, null, CancellationToken.None);
        await _store.SaveAsync(state, CancellationToken.None);
        return state;
    }

    [Fact]
    public async Task The_browsers_notes_are_used_and_the_plan_records_the_browser_tier()
    {
        IPitchTracker[] trackers = [Tracker(), new StubCloudPitchTracker()];
        var job = await NewJobAsync(trackers);
        var run = Pipeline(trackers).RunAsync(job.JobId, CancellationToken.None);

        // Wait for the stage to park, then answer as a browser would.
        IReadOnlyList<NoteEvent> browserNotes = [new NoteEvent(62, 0.25, 0.5, 100)];
        await WaitForParkAsync(run, job.JobId);
        Assert.True(_registry.TryComplete(job.JobId, browserNotes));
        await run;

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
        var stage = final.Plan.Single(p => p.Stage == StageNames.PitchTracking);
        Assert.Equal(ExecutionTier.ClientDelegated, stage.Tier);
        Assert.Equal(nameof(ClientDelegatedPitchTracker), stage.Executor);

        var notes = await _store.ReadArtifactListAsync<NoteEvent>(job.JobId, "notes.json", CancellationToken.None);
        Assert.Equal(62, Assert.Single(notes).MidiPitch);
        Assert.True(_notifier.Saw(JobStage.AwaitingClient), "the browser was never told to start");
    }

    [Fact]
    public async Task A_browser_that_never_answers_times_out_and_the_stage_falls_through()
    {
        IPitchTracker[] trackers = [Tracker(timeoutSeconds: 300), new StubCloudPitchTracker()];
        var job = await NewJobAsync(trackers);
        var run = Pipeline(trackers).RunAsync(job.JobId, CancellationToken.None);

        await WaitForParkAsync(run, job.JobId);
        _time.Advance(TimeSpan.FromSeconds(301));
        await run;

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        // The job must finish, not hang: the next tier down picks the stage up.
        Assert.Equal(JobStage.Complete, final!.Stage);
        var stage = final.Plan.Single(p => p.Stage == StageNames.PitchTracking);
        Assert.Equal(ExecutionTier.Cloud, stage.Tier);
        Assert.Equal(nameof(StubCloudPitchTracker), stage.Executor);
        Assert.False(_registry.IsWaiting(job.JobId), "a waiter was left behind");
    }

    [Fact]
    public async Task With_no_other_tracker_a_silent_browser_fails_the_job_rather_than_hanging()
    {
        IPitchTracker[] trackers = [Tracker(timeoutSeconds: 300)];
        var job = await NewJobAsync(trackers);
        var run = Pipeline(trackers).RunAsync(job.JobId, CancellationToken.None);

        await WaitForParkAsync(run, job.JobId);
        _time.Advance(TimeSpan.FromSeconds(301));
        await run;

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Failed, final!.Stage);
        Assert.Contains("browser", final.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelling_a_parked_job_leaves_no_waiter_behind()
    {
        IPitchTracker[] trackers = [Tracker()];
        var job = await NewJobAsync(trackers);
        using var cts = new CancellationTokenSource();
        var run = Pipeline(trackers).RunAsync(job.JobId, cts.Token);

        await WaitForParkAsync(run, job.JobId);
        await cts.CancelAsync();
        await run;

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Cancelled, final!.Stage);
        Assert.False(_registry.IsWaiting(job.JobId), "a waiter survived cancellation");
    }
}
