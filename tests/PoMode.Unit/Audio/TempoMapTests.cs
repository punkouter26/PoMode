using PoMode.API.Features.Audio;
using Xunit;

namespace PoMode.Unit.Audio;

public class TempoMapTests
{
    private const int SampleRate = 22050;

    private static AudioBuffer ClickTrack(int beats, Func<int, double> bpmAt)
    {
        var times = new List<double>();
        var now = 0.25;
        for (var beat = 0; beat < beats; beat++)
        {
            times.Add(now);
            now += 60.0 / bpmAt(beat);
        }

        var samples = new float[(int)((now + 0.5) * SampleRate)];
        foreach (var time in times)
        {
            var start = (int)(time * SampleRate);
            for (var i = 0; i < 220 && start + i < samples.Length; i++)
            {
                var envelope = 1.0 - (i / 220.0);
                samples[start + i] = (float)(envelope * 0.85
                    * Math.Sin(2 * Math.PI * 1200 * i / SampleRate));
            }
        }

        return new AudioBuffer(samples, SampleRate, Channels: 1);
    }

    [Fact]
    public void Steady_click_track_is_reported_steady_with_consecutive_measures()
    {
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, _ => 120.0));

        Assert.True(map.Measures.Count >= 8);
        Assert.True(map.IsSteady);
        Assert.InRange(map.MedianBpm, 118, 122);
        Assert.Equal(1, map.Measures[0].Number);
        Assert.False(map.Measures[0].Changed);
        Assert.DoesNotContain(map.Measures, m => m.Changed);
    }

    [Fact]
    public void Accelerating_track_is_reported_unsteady_with_detected_changes()
    {
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, beat => 100.0 + beat));

        Assert.False(map.IsSteady);
        Assert.True(map.MaxBpm > map.MinBpm + 10);
        Assert.Contains(map.Measures, m => m.Changed);

        var bpms = map.Measures.Select(m => m.Bpm).ToArray();
        Assert.True(bpms[^1] > bpms[0] + 20);
        Assert.All(map.Measures, m => Assert.InRange(m.Bpm, map.MedianBpm / 2, map.MedianBpm * 2));
    }

    [Fact]
    public void Silence_and_tiny_buffers_yield_empty_maps()
    {
        var silence = new AudioBuffer(new float[SampleRate * 4], SampleRate, Channels: 1);
        Assert.Empty(TempoEstimator.EstimateTempoMap(silence).Measures);

        var tiny = new AudioBuffer(new float[64], SampleRate, Channels: 1);
        Assert.Empty(TempoEstimator.EstimateTempoMap(tiny).Measures);
    }

    [Fact]
    public void Measures_are_strictly_time_ordered()
    {
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, _ => 120.0));
        var times = map.Measures.Select(m => m.StartSec).ToArray();
        Assert.Equal([.. times.Order()], times);
    }
}
