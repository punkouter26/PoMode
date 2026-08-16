using Xunit;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.StemSeparation;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;

namespace PoMode.Unit.Pipeline;

public sealed class FakeExecutorTests : IDisposable
{
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), $"pomode-fake-{Guid.NewGuid():N}");

    public FakeExecutorTests() => Directory.CreateDirectory(_jobDir);

    public void Dispose() => Directory.Delete(_jobDir, recursive: true);

    private StageContext Context()
    {
        var input = Path.Combine(_jobDir, "input.wav");
        File.WriteAllBytes(input, TestAudio.MakeWav());
        return new StageContext("job1", _jobDir, input);
    }

    [Fact]
    public async Task FakeStemSeparator_writes_both_stems()
    {
        var context = Context();
        await new FakeStemSeparator().SeparateAsync(context, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_jobDir, "vocals.wav")));
        Assert.True(File.Exists(Path.Combine(_jobDir, "instrumental.wav")));
    }

    [Fact]
    public async Task FakePitchTracker_returns_deterministic_c_major_scale()
    {
        var notes = await new FakePitchTracker().TrackAsync(Context(), CancellationToken.None);

        Assert.Equal(8, notes.Count);
        Assert.Equal(60, notes[0].MidiPitch);
        Assert.Equal(72, notes[7].MidiPitch);
        Assert.Equal(3.5, notes[7].StartSec);
        Assert.All(notes, n => Assert.Equal(96, n.Velocity));
    }

    [Fact]
    public async Task FakeChordRecognizer_returns_four_two_second_chords()
    {
        var chords = await new FakeChordRecognizer().RecognizeAsync(Context(), CancellationToken.None);

        Assert.Equal(4, chords.Count);
        Assert.Equal(["C", "Am", "F", "G"], chords.Select(c => c.Symbol).ToArray());
        Assert.All(chords, c => Assert.Equal(2.0, c.EndSec - c.StartSec));
        Assert.Equal(8.0, chords[^1].EndSec);
    }

    [Fact]
    public async Task PlaceholderModalAnalyzer_writes_result_json()
    {
        await new PlaceholderModalAnalyzer().AnalyzeAsync(Context(), CancellationToken.None);

        var text = await File.ReadAllTextAsync(Path.Combine(_jobDir, "result.json"));
        Assert.Contains("Phase 3", text);
    }

    [Fact]
    public async Task All_fakes_are_local_tier_and_available()
    {
        IStageExecutor[] executors = [new FakeStemSeparator(), new FakePitchTracker(), new FakeChordRecognizer()];
        foreach (var executor in executors)
        {
            Assert.Equal(ExecutionTier.Local, executor.Tier);
            Assert.True(await executor.IsAvailableAsync(CancellationToken.None));
        }
    }
}
