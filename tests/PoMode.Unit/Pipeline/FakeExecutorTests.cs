using Xunit;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.Separation;
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

}
