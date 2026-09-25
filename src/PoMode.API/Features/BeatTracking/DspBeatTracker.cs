using PoMode.API.Audio;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.BeatTracking;

/// <summary>
/// The classic, model-less beat tracker: <see cref="TempoEstimator"/>'s onset-autocorrelation grid
/// and per-measure tempo map, exactly what the pipeline wrote before the seam existed. It cannot
/// hear downbeats, so it reports none and measure numbers keep assuming 4/4 from t=0.
/// </summary>
public sealed class DspBeatTracker : IBeatTracker
{
    public string Name => nameof(DspBeatTracker);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool IsClassicFallback => true;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<BeatTrackResult> TrackBeatsAsync(StageContext context, CancellationToken ct)
    {
        var audio = context.DecodePreferredAnalysisAudio();
        var grid = TempoEstimator.EstimateGrid(audio);
        var map = TempoEstimator.EstimateTempoMap(audio);
        var beats = new List<double>();
        if (grid.Confidence > 0)
        {
            for (var at = grid.FirstBeatSec; at < audio.DurationSeconds; at += 60.0 / grid.Bpm)
            {
                beats.Add(at);
            }
        }
        return Task.FromResult(new BeatTrackResult(
            new BeatGridDto(grid.Bpm, grid.FirstBeatSec, grid.Confidence, Downbeats: null, Tracker: Name),
            new TempoMapDto(
                map.MedianBpm, map.MinBpm, map.MaxBpm, map.IsSteady, map.Confidence,
                [.. map.Measures.Select(measure => new TempoMeasureDto(
                    measure.Number, measure.StartSec, measure.Bpm, measure.Changed))]),
            beats));
    }
}
