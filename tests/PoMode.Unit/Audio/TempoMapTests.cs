using PoMode.API.Features.Audio;
using Xunit;
using Xunit.Abstractions;

namespace PoMode.Unit.Audio;

/// <summary>
/// The tempo map is only worth having if it can tell a steady performance from a drifting one, so
/// every test here builds a click track whose true tempo is known by construction and checks the
/// map recovers it.
/// </summary>
public class TempoMapTests(ITestOutputHelper output)
{
    private const int SampleRate = 22050;

    /// <summary>
    /// A click track whose beat period is whatever <paramref name="bpmAt"/> says it is at each beat.
    /// Passing a constant gives a machine-steady track; returning a rising value gives an accelerando.
    /// </summary>
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
            // A short decaying burst is a sharp onset the envelope can find, the same shape
            // TestAudio.MakeClickTrack uses.
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
    public void A_steady_click_track_is_reported_as_steady_at_its_true_tempo()
    {
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, _ => 120.0));

        output.WriteLine($"median={map.MedianBpm} min={map.MinBpm} max={map.MaxBpm} "
            + $"steady={map.IsSteady} measures={map.Measures.Count}");

        Assert.True(map.Measures.Count >= 8, $"only {map.Measures.Count} measures were measured");
        Assert.True(map.IsSteady, $"a machine-steady track was called unsteady ({map.MinBpm}-{map.MaxBpm})");
        Assert.InRange(map.MedianBpm, 118, 122);
    }

    [Fact]
    public void Measures_are_numbered_from_one_and_run_in_time_order()
    {
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, _ => 120.0));

        Assert.Equal(1, map.Measures[0].Number);
        Assert.Equal(
            [.. Enumerable.Range(1, map.Measures.Count)],
            [.. map.Measures.Select(measure => measure.Number)]);

        var times = map.Measures.Select(measure => measure.StartSec).ToArray();
        Assert.Equal([.. times.Order()], times);
    }

    [Fact]
    public void The_first_measure_is_never_marked_as_a_change()
    {
        // There is nothing before it to differ from; marking it would put a spurious flag on every song.
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, _ => 120.0));

        Assert.False(map.Measures[0].Changed);
    }

    [Fact]
    public void A_track_that_speeds_up_is_reported_as_unsteady_with_a_rising_tempo()
    {
        // 100 BPM accelerating by 1 BPM per beat: unmistakably not steady by the end.
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, beat => 100.0 + beat));

        output.WriteLine($"median={map.MedianBpm} min={map.MinBpm} max={map.MaxBpm} steady={map.IsSteady}");
        foreach (var measure in map.Measures)
        {
            output.WriteLine($"  bar {measure.Number,2}  {measure.Bpm,6:0.0} BPM  changed={measure.Changed}");
        }

        Assert.False(map.IsSteady, "an accelerating track was reported as steady");
        Assert.True(map.MaxBpm > map.MinBpm + 10,
            $"expected a wide tempo range, got {map.MinBpm}-{map.MaxBpm}");

        // The point of the feature: the measures where it moved are flagged.
        Assert.Contains(map.Measures, measure => measure.Changed);

        // Rising, not merely varying: the back half must be faster than the front half.
        var half = map.Measures.Count / 2;
        var early = map.Measures.Take(half).Average(measure => measure.Bpm);
        var late = map.Measures.Skip(half).Average(measure => measure.Bpm);
        Assert.True(late > early, $"expected acceleration, but early={early:0.0} late={late:0.0}");
    }

    [Fact]
    public void A_one_measure_excursion_is_smoothed_away_instead_of_reported_as_rubato()
    {
        // Where every beat is equally accented the snap has nothing marking beat one, and alternates
        // between neighbouring onsets: a constant-tempo demo track came back as 117/105/117/105 and
        // was reported "not steady". A steady click track must never produce a Changed flag.
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, _ => 120.0));

        output.WriteLine(string.Join(", ", map.Measures.Select(measure => $"{measure.Bpm:0.0}")));

        Assert.DoesNotContain(map.Measures, measure => measure.Changed);
        Assert.True(map.MaxBpm - map.MinBpm <= 2.0,
            $"a constant tempo spread over {map.MinBpm}-{map.MaxBpm} BPM");
    }

    [Fact]
    public void Smoothing_does_not_flatten_a_genuine_ramp()
    {
        // The median of three consecutive rising values is the middle one, so a real accelerando
        // survives the filter that kills the sawtooth.
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, beat => 100.0 + beat));

        var bpms = map.Measures.Select(measure => measure.Bpm).ToArray();
        Assert.True(bpms[^1] > bpms[0] + 20,
            $"the ramp was flattened: {bpms[0]:0.0} to {bpms[^1]:0.0}");
    }

    [Fact]
    public void Silence_yields_an_empty_map_rather_than_an_invented_tempo()
    {
        var silence = new AudioBuffer(new float[SampleRate * 4], SampleRate, Channels: 1);

        var map = TempoEstimator.EstimateTempoMap(silence);

        // "Steady at 120" would be a fabrication; an empty measure list is the honest answer and the
        // client renders nothing for it.
        Assert.Empty(map.Measures);
    }

    [Fact]
    public void A_buffer_too_short_to_frame_yields_an_empty_map()
    {
        var tiny = new AudioBuffer(new float[64], SampleRate, Channels: 1);

        Assert.Empty(TempoEstimator.EstimateTempoMap(tiny).Measures);
    }

    [Fact]
    public void No_measure_reports_a_tempo_outside_half_to_double_the_global_estimate()
    {
        // Guards the snapping bound: without it, a measure whose downbeat latched onto the wrong
        // onset would report a wild tempo and wreck the chart's scale.
        var map = TempoEstimator.EstimateTempoMap(ClickTrack(64, beat => 100.0 + beat));

        Assert.All(map.Measures, measure =>
            Assert.InRange(measure.Bpm, map.MedianBpm / 2, map.MedianBpm * 2));
    }
}
