using System.Text.Json;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Integration;

public sealed class ArtifactModalAnalyzerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), $"pomode-modal-{Guid.NewGuid():N}");

    public ArtifactModalAnalyzerTests() => Directory.CreateDirectory(_jobDir);

    public void Dispose() => Directory.Delete(_jobDir, recursive: true);

    private StageContext Context() => new("job1", _jobDir, Path.Combine(_jobDir, "input.wav"));

    private async Task WriteArtifactsAsync(IReadOnlyList<NoteEvent> notes, IReadOnlyList<ChordSpan> chords)
    {
        await File.WriteAllTextAsync(Path.Combine(_jobDir, "notes.json"), JsonSerializer.Serialize(notes, Json));
        await File.WriteAllTextAsync(Path.Combine(_jobDir, "chords.json"), JsonSerializer.Serialize(chords, Json));
    }

    [Fact]
    public async Task Writes_result_json_from_the_note_and_chord_artifacts()
    {
        await WriteArtifactsAsync(
            [new(62, 0.0, 0.4, 96), new(65, 0.5, 0.4, 96), new(69, 1.0, 0.4, 96), new(71, 1.5, 0.4, 96)],
            [new("Dm7", "D", "min7", 0, 2)]);

        await new ArtifactModalAnalyzer().AnalyzeAsync(Context(), CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(_jobDir, "result.json"));
        var result = JsonSerializer.Deserialize<ModalResult>(text, Json);

        Assert.NotNull(result);
        Assert.Equal(1, result.SchemaVersion);
        Assert.Single(result.Windows);
        Assert.Equal("Dm7", result.Windows[0].ChordSymbol);
    }

    [Fact]
    public async Task Missing_artifacts_produce_an_empty_result_rather_than_throwing()
    {
        await new ArtifactModalAnalyzer().AnalyzeAsync(Context(), CancellationToken.None);

        var result = JsonSerializer.Deserialize<ModalResult>(
            await File.ReadAllTextAsync(Path.Combine(_jobDir, "result.json")), Json);

        Assert.NotNull(result);
        Assert.Empty(result.Windows);
        Assert.Null(result.PrimaryMode);
    }
}
