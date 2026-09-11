using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Practice;

/// <summary>
/// Scored ear-training on the modes: the server issues a phrase, the browser transcribes the voice
/// singing it, and the server says how close it was.
///
/// <para>Nothing here is persisted, the same ruling <c>/api/live/analyze</c> and <c>/stats</c> follow:
/// an attempt is a conversation, not a job. The streak the practice page shows lives in that browser's
/// own storage, because it is a per-viewer convenience and not a claim about the music.</para>
///
/// <para>The split of work is the app's usual one. The browser detects <em>what frequency</em> is
/// sounding (signal processing, allowed client-side, shared with the Live page); the server decides
/// what it <em>means</em> — which note was wanted, whether the singer stayed inside the mode, and
/// every sentence of feedback.</para>
/// </summary>
public static class PracticeEndpoints
{
    public static IEndpointRouteBuilder MapPractice(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/practice").WithTags("Practice");

        // GET, and every parameter optional: an exercise is a pure function of its six values, so the
        // same URL is the same phrase forever and a reload costs nothing.
        group.MapGet("/exercise", Results<Ok<ModeExerciseDto>, BadRequest<string>> (
            [FromQuery] ModeExerciseKind? kind,
            [FromQuery] ScaleMode? mode,
            [FromQuery] int? tonicPitchClass,
            [FromQuery] double? bpm,
            [FromQuery] int? seed,
            [FromQuery] int? octave) =>
        {
            var requested = kind ?? ModeExerciseKind.ScaleRun;
            if (!Enum.IsDefined(requested))
            {
                return TypedResults.BadRequest("Unknown exercise kind.");
            }
            var scale = mode ?? ScaleMode.Dorian;
            if (!Enum.IsDefined(scale))
            {
                return TypedResults.BadRequest("Unknown scale mode.");
            }

            return TypedResults.Ok(ModeExerciseBuilder.Build(
                requested,
                scale,
                tonicPitchClass ?? 2,
                bpm ?? DefaultBpm,
                seed ?? DefaultSeed,
                octave ?? DefaultOctave));
        })
        .WithName("GetModeExercise")
        .WithSummary("Issues a deterministic ear-training phrase for one mode, tonic and tempo.");

        // POST because the sung notes are the request, not an address. The attempt carries the six
        // values that regenerate the phrase rather than echoing it back, so the grading always runs
        // against what the server issued.
        group.MapPost("/attempt", Results<Ok<ExerciseScoreDto>, BadRequest<string>> (
            ExerciseAttemptRequest request) =>
        {
            if (!Enum.IsDefined(request.Kind))
            {
                return TypedResults.BadRequest("Unknown exercise kind.");
            }
            if (!Enum.IsDefined(request.Mode))
            {
                return TypedResults.BadRequest("Unknown scale mode.");
            }

            var sung = request.SungNotes ?? [];
            // The same validator the client-delegated pitch results go through: these notes come from
            // a browser too, and "the browser detected it" is not a reason to trust the numbers.
            if (ClientResultValidator.Validate(sung, trackDurationSec: null) is { } problem)
            {
                return TypedResults.BadRequest(problem);
            }

            var exercise = ModeExerciseBuilder.Build(
                request.Kind,
                request.Mode,
                request.TonicPitchClass,
                request.Bpm <= 0 ? DefaultBpm : request.Bpm,
                request.Seed,
                request.Octave <= 0 ? DefaultOctave : request.Octave);

            return TypedResults.Ok(ModeExerciseGrader.Grade(exercise, sung));
        })
        .RequireAuthorization()
        .WithName("GradeModeExercise")
        .WithSummary("Scores a sung attempt against the exercise phrase the server issued.");

        return app;
    }

    /// <summary>Slow enough that an untrained voice can find each note before the next click.</summary>
    private const double DefaultBpm = 72.0;

    /// <summary>Middle of a comfortable singing range for both voices; the page offers ±1.</summary>
    private const int DefaultOctave = 4;

    /// <summary>Only reached when a caller omits the seed entirely — the page always sends one, since
    /// rerolling the seed is how it hands out a different phrase in the same mode.</summary>
    private const int DefaultSeed = 42;
}
