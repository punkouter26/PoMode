using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;

namespace PoMode.Integration;

public sealed class JobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-store-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

    private JobStore Store => new(
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
        _clock);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Create_writes_input_file_and_state_json()
    {
        var store = Store;
        using var content = new MemoryStream(TestAudio.MakeWav());

        var state = await store.CreateAsync("song.wav", content, CancellationToken.None);

        Assert.True(File.Exists(store.InputPath(state)));
        Assert.True(File.Exists(Path.Combine(store.JobDir(state.JobId), "job.json")));
        Assert.Equal(JobStage.Uploaded, state.Stage);
        Assert.Equal(_clock.GetUtcNow(), state.CreatedAt);
    }

    [Fact]
    public async Task Save_then_Load_round_trips_all_mutable_fields()
    {
        var store = Store;
        using var content = new MemoryStream(TestAudio.MakeWav());
        var state = await store.CreateAsync("song.wav", content, CancellationToken.None);

        state.Stage = JobStage.ChordDetecting;
        state.Plan = [new StagePlan("Separating", ExecutionTier.Local, "FakeStemSeparator")];
        state.CompletedStages = ["Separating", "PitchTracking"];
        state.Error = null;
        await store.SaveAsync(state, CancellationToken.None);

        var loaded = await store.LoadAsync(state.JobId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(JobStage.ChordDetecting, loaded.Stage);
        // Progress is derived from the completed stages (2 of 4), never stored.
        Assert.Equal(0.5, loaded.Progress);
        Assert.Equal("FakeStemSeparator", loaded.Plan[0].Executor);
        Assert.Equal(["Separating", "PitchTracking"], loaded.CompletedStages);
    }

    [Fact]
    public async Task Purge_removes_only_jobs_older_than_max_age()
    {
        var store = Store;
        using var old = new MemoryStream(TestAudio.MakeWav());
        var oldJob = await store.CreateAsync("old.wav", old, CancellationToken.None);

        _clock.Advance(TimeSpan.FromDays(8));
        using var fresh = new MemoryStream(TestAudio.MakeWav());
        var freshJob = await store.CreateAsync("fresh.wav", fresh, CancellationToken.None);

        var purged = store.PurgeOlderThan(TimeSpan.FromDays(7));

        Assert.Equal(1, purged);
        Assert.False(Directory.Exists(store.JobDir(oldJob.JobId)));
        Assert.True(Directory.Exists(store.JobDir(freshJob.JobId)));
    }
}
