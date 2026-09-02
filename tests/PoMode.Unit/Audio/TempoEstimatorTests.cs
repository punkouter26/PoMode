using PoMode.API.Features.Audio;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Unit.Audio;

public sealed class TempoEstimatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-tempo-{Guid.NewGuid():N}");

    public TempoEstimatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AudioBuffer Click(double bpm, double seconds = 20.0)
    {
        var path = Path.Combine(_dir, $"click-{bpm}.wav");
        File.WriteAllBytes(path, TestAudio.MakeClickTrack(seconds, bpm));
        return AudioDecoder.Decode(path);
    }

    [Fact]
    public void Finds_tempo_of_click_tracks_and_falls_back_on_silence()
    {
        var estimate120 = TempoEstimator.Estimate(Click(120.0));
        Assert.InRange(estimate120.Bpm, 117, 123);
        Assert.True(estimate120.Confidence > 0);

        var estimate160 = TempoEstimator.Estimate(Click(160.0));
        Assert.InRange(estimate160.Bpm, 157, 163);

        var silence = TempoEstimator.Estimate(new AudioBuffer(new float[22050 * 5], 22050, 1));
        Assert.Equal(120.0, silence.Bpm);
        Assert.Equal(0.0, silence.Confidence);
    }

    [Fact]
    public void Beat_grid_phase_lands_accurately_on_clicks()
    {
        var grid = TempoEstimator.EstimateGrid(Click(120.0));
        Assert.InRange(grid.Bpm, 117, 123);
        Assert.True(grid.Confidence > 0);
        var period = 60.0 / grid.Bpm;
        var phase = Math.Min(grid.FirstBeatSec % period, period - (grid.FirstBeatSec % period));
        Assert.True(phase < 0.12);
    }
}
