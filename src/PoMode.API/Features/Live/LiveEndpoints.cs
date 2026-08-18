using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.Visualization;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Live;

/// <summary>
/// Near-real-time analysis of live microphone input. The browser transcribes its own mic (same
/// trust model as Tier 2) and posts note events; every musical decision — tonic, modes, note
/// roles, labels — happens here, through the same engine and builder a stored job uses. Nothing
/// is persisted: a live take is a conversation, not a job.
/// </summary>
public static class LiveEndpoints
{
    /// <summary>Windows the mode engine scores. A live take has no chord track, so fixed-width
    /// spans stand in for chord boundaries.</summary>
    private const double WindowSeconds = 4.0;

    public static IEndpointRouteBuilder MapLive(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/live/analyze", Results<Ok<LiveAnalysisDto>, BadRequest<string>> (
            LiveAnalyzeRequest request) =>
        {
            var posted = request.Notes ?? [];
            if (ClientResultValidator.Validate(posted, trackDurationSec: null) is { } problem)
            {
                return TypedResults.BadRequest(problem);
            }
            var notes = posted.OrderBy(note => note.StartSec).ToArray();

            var duration = notes.Length == 0
                ? 0.0
                : notes.Max(note => note.StartSec + note.DurationSec);
            var windows = SyntheticWindows(duration);

            var result = ModalAnalysisEngine.Analyze(notes, windows, tempoBpm: 120.0, tempoEstimated: true);
            var visual = VisualizationBuilder.Build(notes, windows, result);
            return TypedResults.Ok(new LiveAnalysisDto(result, visual));
        }).RequireAuthorization();

        return app;
    }

    /// <summary>Fixed 4-second spans with the no-chord root, so the engine windows the take
    /// without inventing harmony that was never played.</summary>
    private static List<ChordSpan> SyntheticWindows(double duration)
    {
        var spans = new List<ChordSpan>();
        for (var start = 0.0; start < duration; start += WindowSeconds)
        {
            var end = Math.Min(start + WindowSeconds, duration);
            if (end - start > 0.25)
            {
                spans.Add(new ChordSpan("—", "N", "", start, end));
            }
        }
        return spans;
    }
}
