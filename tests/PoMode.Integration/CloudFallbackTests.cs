using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The point of Phase 7: when the local separator cannot do the job, the stage lands on a paid tier,
/// and the recorded plan says so loudly enough for the UI to badge it ☁️💰. The cloud stand-in here is
/// a fake — the real providers' protocols are covered by their own fixture-server tests — because what
/// is under test is the pipeline's tier behaviour, not HTTP.
/// </summary>
public sealed class CloudFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-cloudfb-{Guid.NewGuid():N}");
    private readonly JobStore _store;

    public CloudFallbackTests()
        => _store = new JobStore(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
            TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class SilentNotifier : IAnalysisNotifier
    {
        public Task PublishAsync(JobStatusDto status, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A local separator that always fails, standing in for a missing model or an OOM.</summary>
    private sealed class FailingLocalSeparator : IStemSeparator
    {
        public string Name => nameof(FailingLocalSeparator);
        public ExecutionTier Tier => ExecutionTier.Local;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task SeparateAsync(StageContext context, CancellationToken ct)
            => throw new InvalidOperationException("simulated local failure");
    }

    /// <summary>Stands in for Replicate/LALAL.AI: paid tier, gated on a credential.</summary>
    private sealed class StubCloudSeparator(bool available) : IStemSeparator
    {
        private readonly FakeStemSeparator _writer = new();
        public int Calls { get; private set; }
        public string Name => nameof(StubCloudSeparator);
        public ExecutionTier Tier => ExecutionTier.Cloud;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(available);
        public Task SeparateAsync(StageContext context, CancellationToken ct)
        {
            Calls++;
            return _writer.SeparateAsync(context, ct);
        }
    }

    private AnalysisPipeline Pipeline(IStemSeparator[] separators)
    {
        IPitchTracker[] trackers = [new FakePitchTracker()];
        IChordRecognizer[] chords = [new ChromaChordRecognizer()];
        return new AnalysisPipeline(
            _store,
            new ExecutionPlanner(separators, trackers, chords),
            separators, trackers, chords,
            new ArtifactModalAnalyzer(_store, NullLogger<ArtifactModalAnalyzer>.Instance),
            new SilentNotifier(),
            NullLogger<AnalysisPipeline>.Instance);
    }

    private async Task<JobState> NewJobAsync()
    {
        using var content = new MemoryStream(TestAudio.MakeWav());
        return await _store.CreateAsync("song.wav", content, CancellationToken.None);
    }

    [Fact]
    public async Task A_failing_local_stage_lands_on_the_cloud_tier_and_the_plan_records_it()
    {
        var cloud = new StubCloudSeparator(available: true);
        IStemSeparator[] separators = [new FailingLocalSeparator(), cloud];
        var job = await NewJobAsync();

        await Pipeline(separators).RunAsync(job.JobId, CancellationToken.None);

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Complete, final!.Stage);
        var separating = final.Plan.Single(stage => stage.Stage == StageNames.Separating);
        Assert.Equal(ExecutionTier.Cloud, separating.Tier);
        Assert.Equal(nameof(StubCloudSeparator), separating.Executor);
        Assert.Equal(1, cloud.Calls);
        Assert.True(File.Exists(Path.Combine(_store.JobDir(job.JobId), "vocals.wav")));
    }

    [Fact]
    public async Task The_local_tier_is_still_preferred_when_it_works()
    {
        var cloud = new StubCloudSeparator(available: true);
        IStemSeparator[] separators = [new FakeStemSeparator(), cloud];
        var job = await NewJobAsync();

        await Pipeline(separators).RunAsync(job.JobId, CancellationToken.None);

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        var separating = final!.Plan.Single(stage => stage.Stage == StageNames.Separating);
        Assert.Equal(ExecutionTier.Local, separating.Tier);
        // Nothing was spent: a working local tier means the paid executor is never touched.
        Assert.Equal(0, cloud.Calls);
    }

    [Fact]
    public async Task With_the_cloud_tier_unavailable_the_job_fails_rather_than_spending_money()
    {
        // This is what Cloud:Enabled=false and an absent key both look like to the planner.
        var cloud = new StubCloudSeparator(available: false);
        IStemSeparator[] separators = [new FailingLocalSeparator(), cloud];
        var job = await NewJobAsync();

        await Pipeline(separators).RunAsync(job.JobId, CancellationToken.None);

        var final = await _store.LoadAsync(job.JobId, CancellationToken.None);
        Assert.Equal(JobStage.Failed, final!.Stage);
        Assert.Equal(0, cloud.Calls);
        Assert.Contains("simulated local failure", final.Error);
    }

    [Fact]
    public async Task Planning_prefers_local_over_cloud_regardless_of_registration_order()
    {
        // TierRank decides precedence, not DI order, so a cloud executor registered first must still
        // lose to an available local one.
        IStemSeparator[] separators = [new StubCloudSeparator(available: true), new FakeStemSeparator()];
        IPitchTracker[] trackers = [new FakePitchTracker()];
        IChordRecognizer[] chords = [new ChromaChordRecognizer()];

        var plan = await new ExecutionPlanner(separators, trackers, chords)
            .PlanAsync(CancellationToken.None);

        var separating = plan.Single(stage => stage.Stage == StageNames.Separating);
        Assert.Equal(ExecutionTier.Local, separating.Tier);
        Assert.Equal(nameof(FakeStemSeparator), separating.Executor);
    }

    [Fact]
    public async Task Both_real_cloud_separators_report_unavailable_with_no_keys_configured()
    {
        // The default posture of this repository and of a fresh clone: no keys, so the paid tier is
        // simply not there, and no configuration mistake can make a job spend money by accident.
        var configuration = new ConfigurationBuilder().Build();
        var credentials = new PoMode.API.Features.Cloud.CloudCredentials(configuration);
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions
            .AddHttpClient(services);
        var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
            .BuildServiceProvider(services);
        var factory = (IHttpClientFactory)provider.GetService(typeof(IHttpClientFactory))!;

        var replicate = new ReplicateStemSeparator(
            credentials, configuration, factory, TimeProvider.System,
            NullLogger<ReplicateStemSeparator>.Instance);
        var lalal = new LalalStemSeparator(
            credentials, configuration, factory, TimeProvider.System,
            NullLogger<LalalStemSeparator>.Instance);

        Assert.False(await replicate.IsAvailableAsync(CancellationToken.None));
        Assert.False(await lalal.IsAvailableAsync(CancellationToken.None));
    }
}
