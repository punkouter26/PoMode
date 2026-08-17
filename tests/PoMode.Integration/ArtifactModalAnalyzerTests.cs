using Microsoft.Extensions.Configuration;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Integration;

public sealed class ArtifactModalAnalyzerTests : IDisposable
{
    private const string JobId = "job1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-modal-{Guid.NewGuid():N}");

    private JobStore Store => new(
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Jobs:RootPath"] = _root }).Build(),
        TimeProvider.System);

    public ArtifactModalAnalyzerTests() => Directory.CreateDirectory(Path.Combine(_root, JobId));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private StageContext Context() => new(JobId, Path.Combine(_root, JobId), Path.Combine(_root, JobId, "input.wav"));

    private static async Task WriteArtifactsAsync(JobStore store, IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords)
    {
        await store.WriteArtifactAsync(JobId, "notes.json", notes, CancellationToken.None);
        await store.WriteArtifactAsync(JobId, "chords.json", chords, CancellationToken.None);
    }

    [Fact]
    public async Task Writes_result_json_from_the_note_and_chord_artifacts()
    {
        var store = Store;
        await WriteArtifactsAsync(store,
            [new(62, 0.0, 0.4, 96), new(65, 0.5, 0.4, 96), new(69, 1.0, 0.4, 96), new(71, 1.5, 0.4, 96)],
            [new("Dm7", "D", "min7", 0, 2)]);

        await new ArtifactModalAnalyzer(store).AnalyzeAsync(Context(), CancellationToken.None);

        var result = await store.ReadArtifactAsync<ModalResult>(JobId, "result.json", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Single(result.Windows);
        Assert.Equal("Dm7", result.Windows[0].ChordSymbol);
    }

    [Fact]
    public async Task Missing_artifacts_produce_an_empty_result_rather_than_throwing()
    {
        var store = Store;
        await new ArtifactModalAnalyzer(store).AnalyzeAsync(Context(), CancellationToken.None);

        var result = await store.ReadArtifactAsync<ModalResult>(JobId, "result.json", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Windows);
        Assert.Null(result.PrimaryMode);
    }
}
