using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.Separation;
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
    private readonly ClientWorkRegistry<IReadOnlyList<NoteEvent>> _registry;
    private readonly RecordingNotifier _notifier = new();

    public ClientDelegatedFallbackTests()
    {
        _store = new JobStore(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
            TimeProvider.System);
        _registry = new ClientWorkRegistry<IReadOnlyList<NoteEvent>>(_time);
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
    /// A classic model-less DSP stand-in, the same shape as YinPitchTracker. The fall-through
    /// target has to rank BELOW ClientDelegated or the stage never parks at all: a plain Local
    /// tracker would outrank the browser, which is what an earlier version of these tests got
    /// wrong. IsClassicFallback is what puts it below — tier alone cannot, now that the unused
    /// paid tier this used to borrow is gone.
    /// </summary>
    private sealed class StubFallbackPitchTracker : IPitchTracker
    {
        public string Name => nameof(StubFallbackPitchTracker);
        public ExecutionTier Tier => ExecutionTier.Local;
        public bool IsClassicFallback => true;
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
    /// first � otherwise a pipeline that threw before parking would spin this loop forever.
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
        IPitchTracker[] trackers = [Tracker(), new StubFallbackPitchTracker()];
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
        IPitchTracker[] trackers = [Tracker(timeoutSeconds: 300), new StubFallbackPitchTracker()];
        var job = await NewJobAsync(trackers);
        var run = Pipeline(trackers).RunAsync(job.JobId, CancellationToken.None);

        await WaitForParkAsync(run, job.JobId);
        _time.Advance(TimeSpan.FromSeconds(301));
        await run;

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        // The job must finish, not hang: the next tier down picks the stage up.
        Assert.Equal(JobStage.Complete, final!.Stage);
        var stage = final.Plan.Single(p => p.Stage == StageNames.PitchTracking);
        Assert.Equal(ExecutionTier.Local, stage.Tier);
        Assert.Equal(nameof(StubFallbackPitchTracker), stage.Executor);
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

    [Fact]
    public async Task A_browser_separation_derives_the_instrumental_and_a_decline_falls_through_at_once()
    {
        var stems = new ClientWorkRegistry<ClientStems>(_time);
        IPitchTracker[] trackers = [new StubFallbackPitchTracker()];
        IStemSeparator[] separators =
        [
            new ClientDelegatedStemSeparator(stems, _store, _notifier,
                new ConfigurationBuilder().Build(), NullLogger<ClientDelegatedStemSeparator>.Instance),
            new FakeStemSeparator(),
        ];
        IChordRecognizer[] chords = [new ChromaChordRecognizer()];
        var planner = new ExecutionPlanner(separators, trackers, chords);
        var pipeline = new AnalysisPipeline(_store, planner, separators, trackers, chords,
            new ArtifactModalAnalyzer(_store, NullLogger<ArtifactModalAnalyzer>.Instance),
            _notifier, NullLogger<AnalysisPipeline>.Instance);

        async Task<(JobState Job, Task Run)> StartAsync()
        {
            // Past the 30 s short-clip line, under which no separator is asked at all.
            using var content = new MemoryStream(TestAudio.MakeTwoToneStereo(31.0, 220, 330));
            var job = await _store.CreateAsync("song.wav", content, CancellationToken.None);
            job.Plan = await planner.PlanAsync(browserCanInfer: true, null, CancellationToken.None);
            await _store.SaveAsync(job, CancellationToken.None);
            var run = pipeline.RunAsync(job.JobId, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!stems.IsWaiting(job.JobId))
            {
                Assert.False(run.IsCompleted || DateTime.UtcNow > deadline, "separation never parked");
                await Task.Delay(10);
            }
            return (job, run);
        }

        // The browser answers with half the mix as "vocals": the other half must be the instrumental.
        var (answered, run) = await StartAsync();
        var mix = PoMode.API.Audio.AudioDecoder.Decode(_store.InputPath(answered));
        var vocalsPath = Path.Combine(_store.JobDir(answered.JobId), "vocals.wav");
        PoMode.API.Audio.WavWriter.Write(vocalsPath, mix with { Samples = [.. mix.Samples.Select(sample => sample / 2)] });
        Assert.True(stems.TryComplete(answered.JobId, new ClientStems(vocalsPath)));
        await run;

        var final = await _store.LoadAsync(answered.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
        Assert.Equal(nameof(ClientDelegatedStemSeparator), final.Plan.Single(p => p.Stage == StageNames.Separating).Executor);
        var instrumental = PoMode.API.Audio.AudioDecoder.Decode(Path.Combine(_store.JobDir(answered.JobId), "instrumental.wav"));
        Assert.Equal(mix.Samples.Length, instrumental.Samples.Length);
        Assert.All(Enumerable.Range(0, mix.Samples.Length).Where(i => i % 97 == 0),
            i => Assert.Equal(mix.Samples[i] / 2, instrumental.Samples[i], 0.002));

        // A browser that declines is not waited for: no clock advance, and the placeholder takes over.
        var (declined, declinedRun) = await StartAsync();
        Assert.True(stems.TryDecline(declined.JobId, "no WebGPU"));
        await declinedRun;
        var fellThrough = await _store.LoadAsync(declined.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, fellThrough!.Stage);
        Assert.Equal(nameof(FakeStemSeparator), fellThrough.Plan.Single(p => p.Stage == StageNames.Separating).Executor);
    }
}
