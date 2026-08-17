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

    [Theory]
    [InlineData(90.0)]
    [InlineData(120.0)]
    [InlineData(140.0)]
    public void Finds_the_tempo_of_a_click_track(double bpm)
    {
        var estimate = TempoEstimator.Estimate(Click(bpm));

        // Octave errors (half/double time) are the classic failure; assert the true tempo,
        // and let the implementation pick the octave inside the min/max band.
        Assert.InRange(estimate.Bpm, bpm - 3, bpm + 3);
        Assert.True(estimate.Confidence > 0);
    }

    [Fact]
    public void Silence_falls_back_to_120_with_zero_confidence()
    {
        var estimate = TempoEstimator.Estimate(new AudioBuffer(new float[22050 * 5], 22050, 1));

        Assert.Equal(120.0, estimate.Bpm);
        Assert.Equal(0.0, estimate.Confidence);
    }

    [Fact]
    public void Very_short_input_falls_back_rather_than_throwing()
    {
        var estimate = TempoEstimator.Estimate(new AudioBuffer(new float[512], 22050, 1));

        Assert.Equal(120.0, estimate.Bpm);
        Assert.Equal(0.0, estimate.Confidence);
    }

    [Fact]
    public void Estimates_stay_inside_the_requested_band()
    {
        var estimate = TempoEstimator.Estimate(Click(120.0), minBpm: 100, maxBpm: 130);

        Assert.InRange(estimate.Bpm, 100, 130);
    }
}
