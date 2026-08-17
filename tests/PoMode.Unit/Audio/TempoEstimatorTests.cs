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
    [InlineData(75.0)]
    [InlineData(90.0)]
    [InlineData(120.0)]
    [InlineData(140.0)]
    [InlineData(160.0)]
    [InlineData(180.0)]
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

    [Fact]
    public void The_beat_grid_phase_lands_on_the_clicks()
    {
        // Clicks start at t=0, so the first beat must sit at (or within one envelope frame of) 0.
        var grid = TempoEstimator.EstimateGrid(Click(120.0));

        Assert.InRange(grid.Bpm, 117, 123);
        Assert.True(grid.Confidence > 0);
        // The onset envelope rises as soon as an analysis window first touches a click, so the
        // estimated phase leads the audible click by up to two 1024-sample windows (~0.09 s) —
        // smaller than one chroma frame (0.093 s), so harmless for beat-snapped chord boundaries.
        var period = 60.0 / grid.Bpm;
        var phase = Math.Min(grid.FirstBeatSec % period, period - (grid.FirstBeatSec % period));
        Assert.True(phase < 0.12, $"first beat at {grid.FirstBeatSec:0.###}s is {phase:0.###}s off the click grid");
    }

    [Fact]
    public void The_beat_grid_phase_follows_a_shifted_click_track()
    {
        // Prepend 0.25 s of silence: every click moves 0.25 s later, and so must the phase.
        var click = Click(120.0);
        var lead = new float[(int)(0.25 * click.SampleRate)];
        var shifted = new AudioBuffer([.. lead, .. click.Samples], click.SampleRate, click.Channels);

        var grid = TempoEstimator.EstimateGrid(shifted);

        Assert.True(grid.Confidence > 0);
        // Same lead tolerance as above: the envelope anticipates the click by up to ~0.09 s.
        var period = 60.0 / grid.Bpm;
        var offset = Math.Abs(grid.FirstBeatSec - 0.25) % period;
        var phase = Math.Min(offset, period - offset);
        Assert.True(phase < 0.12, $"first beat at {grid.FirstBeatSec:0.###}s is {phase:0.###}s off the shifted grid");
    }

    [Fact]
    public void A_zero_confidence_estimate_yields_a_zero_confidence_grid()
    {
        var grid = TempoEstimator.EstimateGrid(new AudioBuffer(new float[22050], 22050, 1));

        Assert.Equal(0.0, grid.Confidence);
    }
}
