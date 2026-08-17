using Microsoft.Extensions.Configuration;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

public sealed class JobStoreArtifactTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-artifact-{Guid.NewGuid():N}");

    private JobStore Store => new(
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
        TimeProvider.System);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<string> NewJobAsync(JobStore store)
    {
        using var content = new MemoryStream(TestAudio.MakeWav());
        return (await store.CreateAsync("song.wav", content, CancellationToken.None)).JobId;
    }

    [Fact]
    public async Task Artifacts_round_trip_through_the_store()
    {
        var store = Store;
        var jobId = await NewJobAsync(store);
        List<NoteEvent> notes = [new(60, 0, 0.5, 96)];

        await store.WriteArtifactAsync(jobId, "notes.json", notes, CancellationToken.None);
        var back = await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", CancellationToken.None);

        Assert.Single(back);
        Assert.Equal(60, back[0].MidiPitch);
    }

    [Fact]
    public async Task Missing_artifact_reads_as_empty_or_null_not_a_throw()
    {
        var store = Store;
        var jobId = await NewJobAsync(store);

        Assert.Empty(await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", CancellationToken.None));
        Assert.Null(await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", CancellationToken.None));
        Assert.Null(await store.ReadArtifactBytesAsync(jobId, "vocals.wav", CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_artifact_reads_as_null_not_a_throw()
    {
        var store = Store;
        var jobId = await NewJobAsync(store);
        await File.WriteAllTextAsync(Path.Combine(store.JobDir(jobId), "result.json"), "{ not json");

        Assert.Null(await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_reads_and_writes_of_one_artifact_never_throw()
    {
        var store = Store;
        var jobId = await NewJobAsync(store);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 60 && !cts.IsCancellationRequested; i++)
            {
                List<NoteEvent> notes = [.. Enumerable.Range(0, 200).Select(n => new NoteEvent(60 + (n % 12), n * 0.1, 0.4, 96))];
                await store.WriteArtifactAsync(jobId, "notes.json", notes, cts.Token);
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 60 && !cts.IsCancellationRequested; i++)
            {
                await store.ReadArtifactBytesAsync(jobId, "notes.json", cts.Token);
                await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", cts.Token);
            }
        })).ToArray();

        // The whole point: no UnauthorizedAccessException / IOException escapes.
        await Task.WhenAll([writer, .. readers]);
    }
}
