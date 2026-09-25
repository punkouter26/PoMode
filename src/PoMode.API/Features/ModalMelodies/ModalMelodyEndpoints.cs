using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Auth;
using PoMode.API.Features.Export;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalMelodies;

public static class ModalMelodyEndpoints
{
    public static IEndpointRouteBuilder MapModalMelodies(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/modal-melodies")
            .WithTags("ModalMelodies");

        group.MapGet("/progressions", (ModalMelodyGenerator generator) =>
        {
            return TypedResults.Ok(generator.GetProgressions());
        })
        .WithName("GetModalProgressions")
        .WithSummary("Retrieves standard chord progression presets across genres and modes.");

        group.MapPost("/generate", (ModalMelodyRequest request, ModalMelodyGenerator generator) =>
        {
            var result = generator.Generate(request);
            return TypedResults.Ok(result);
        })
        .WithName("GenerateModalMelody")
        .WithSummary("Generates an algorithmic melody and chord accompaniment for a scale mode and progression.");

        group.MapGet("/midi", (
            [FromQuery] int? tonicPitchClass,
            [FromQuery] ScaleMode? mode,
            [FromQuery] string? progressionId,
            [FromQuery] double? bpm,
            [FromQuery] MelodyStyle? style,
            [FromQuery] int? seed,
            [FromQuery] double? targetPurity,
            ModalMelodyGenerator generator) =>
        {
            var request = FromQuery(tonicPitchClass, mode, progressionId, bpm, style, seed, targetPurity);

            var generated = generator.Generate(request);
            var midiBytes = MidiFileBuilder.Build(
                notes: generated.MelodyNotes,
                chords: generated.Chords,
                result: generated.ModalAnalysis);

            var fileName = $"modal-{generated.TonicPitchClass}-{generated.Mode}-{generated.ProgressionId}.mid";
            return TypedResults.File(midiBytes, "audio/midi", fileName);
        })
        .WithName("ExportModalMelodyMidiGet")
        .WithSummary("Exports the generated modal melody and chord track as a standard MIDI file (GET).");

        group.MapGet("/wav", (
            [FromQuery] int? tonicPitchClass,
            [FromQuery] ScaleMode? mode,
            [FromQuery] string? progressionId,
            [FromQuery] double? bpm,
            [FromQuery] MelodyStyle? style,
            [FromQuery] int? seed,
            [FromQuery] double? targetPurity,
            ModalMelodyGenerator generator) =>
        {
            var request = FromQuery(tonicPitchClass, mode, progressionId, bpm, style, seed, targetPurity);

            var generated = generator.Generate(request);
            var duration = generated.Chords.Count > 0 ? generated.Chords[^1].EndSec : 8.0;
            var wavBytes = ModalWavSynthesizer.Synthesize(
                melodyNotes: generated.MelodyNotes,
                chords: generated.Chords,
                totalDurationSec: duration);

            var fileName = $"modal-{generated.TonicPitchClass}-{generated.Mode}-{generated.ProgressionId}.wav";
            return TypedResults.File(wavBytes, "audio/wav", fileName);
        })
        .WithName("ExportModalMelodyWavGet")
        .WithSummary("Synthesizes and exports the generated modal melody and chords as a 44.1kHz 16-bit PCM WAV file (GET).");

        group.MapPost("/analyze", async (
            HttpContext context,
            ModalMelodyRequest request,
            ModalMelodyGenerator generator,
            AnalysisIntake intake,
            CancellationToken ct) =>
        {
            var generated = generator.Generate(request);
            var duration = generated.Chords.Count > 0 ? generated.Chords[^1].EndSec : 8.0;
            var wavBytes = ModalWavSynthesizer.Synthesize(
                melodyNotes: generated.MelodyNotes,
                chords: generated.Chords,
                totalDurationSec: duration);

            var fileName = $"ModeLab_{generated.Mode}_{generated.ProgressionId}.wav";
            using var stream = new MemoryStream(wavBytes);

            var state = await intake.StartAsync(
                fileName: fileName,
                content: stream,
                clientCanInfer: false,
                ct: ct,
                ownerId: PoUser.IdOf(context.User));

            return TypedResults.Ok(state.ToDto());
        })
        .RequireAuthorization()
        .WithName("AnalyzeModalMelodyInAnalyzer")
        .WithSummary("Synthesizes the melody and chords into WAV audio and queues an end-to-end analysis job in the Song Analyzer.");

        // A hum take: the user's own voice over a Mode Lab progression. The recording arrives as a
        // multipart file and the backing rides on the query string exactly as it does for /wav and
        // /midi, which is what lets the server put the chords the user actually heard under their
        // melody instead of asking a recognizer to find harmony in a solo hum.
        group.MapPost("/hum", async Task<Results<Ok<JobStatusDto>, BadRequest<string>>> (
            HttpRequest httpRequest,
            [FromQuery] int? tonicPitchClass,
            [FromQuery] ScaleMode? mode,
            [FromQuery] string? progressionId,
            [FromQuery] double? bpm,
            [FromQuery] MelodyStyle? style,
            [FromQuery] int? seed,
            [FromQuery] double? targetPurity,
            ModalMelodyGenerator generator,
            HumTakeSeeder seeder,
            AnalysisIntake intake,
            CancellationToken ct) =>
        {
            if (!httpRequest.HasFormContentType)
            {
                return TypedResults.BadRequest("Expected a multipart form upload.");
            }

            IFormCollection form;
            try
            {
                form = await httpRequest.ReadFormAsync(ct);
            }
            catch (InvalidDataException)
            {
                // A malformed multipart body fails the parser before any file can be inspected —
                // same treatment as no file at all, matching the upload endpoint.
                return TypedResults.BadRequest("No recording uploaded.");
            }

            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                return TypedResults.BadRequest("No recording uploaded.");
            }
            // The browser encodes the take itself, so it is untrusted audio like any other upload
            // and goes through the same size cap and header sniff.
            if (await AudioFormatValidator.ValidateAsync(file, ct) is { } rejection)
            {
                return TypedResults.BadRequest(rejection switch
                {
                    UploadRejection.TooLarge => "The recording exceeds the 100 MB limit.",
                    UploadRejection.UnsupportedFormat => "The recording must be .wav or .mp3 audio.",
                    _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection, "Unhandled upload rejection."),
                });
            }

            var backing = FromQuery(tonicPitchClass, mode, progressionId, bpm, style, seed, targetPurity);
            var progression = generator.GetProgression(backing.ProgressionId);
            var fileName = $"Hum_{backing.Mode}_{progression.Id}.wav";

            await using var stream = file.OpenReadStream();
            var state = await intake.StartAsync(
                fileName: fileName,
                content: stream,
                clientCanInfer: false,
                ct: ct,
                seed: (job, token) => seeder.SeedAsync(job, backing, token),
                ownerId: PoUser.IdOf(httpRequest.HttpContext.User));

            return TypedResults.Ok(state.ToDto());
        })
        .RequireAuthorization()
        .DisableAntiforgery()
        .WithName("AnalyzeHumTake")
        .WithSummary("Stores a sung/hummed take recorded over a Mode Lab progression and queues it for analysis against that progression's chords.");

        // The two reads below are the hum takes read back to the person who sang them, so both are
        // scoped to the caller the same way the library is. They sit here rather than under
        // /api/analysis because what they are about is the backing — a mode and a progression, or
        // a mode to fit to a voice — and the jobs are only where the answers are stored.
        group.MapGet("/takes", async Task<Ok<TakeHistoryDto>> (
            HttpContext context,
            [FromQuery] ScaleMode? mode,
            [FromQuery] string? progressionId,
            [FromQuery] double? targetPurity,
            HumTakeHistory history,
            CancellationToken ct) =>
        {
            var backing = FromQuery(null, mode, progressionId, null, null, null, targetPurity);
            return TypedResults.Ok(await history.ForBackingAsync(PoUser.IdOf(context.User), backing, ct));
        })
        .RequireAuthorization()
        .WithName("GetHumTakeHistory")
        .WithSummary("The caller's earlier hum takes over one mode and progression, with the mode the analyzer found for each.");

        group.MapGet("/voice", async Task<Ok<VocalRangeDto>> (
            HttpContext context,
            [FromQuery] ScaleMode? mode,
            HumTakeHistory history,
            CancellationToken ct) =>
            TypedResults.Ok(await history.VocalRangeAsync(PoUser.IdOf(context.User), mode ?? ScaleMode.Ionian, ct)))
        .RequireAuthorization()
        .WithName("GetVocalRange")
        .WithSummary("The caller's comfortable sung range across their finished hum takes, and the key that fits a mode to it.");

        return app;
    }

    /// <summary>
    /// The backing parameters as the export and hum endpoints receive them on the query string,
    /// with the same defaults for values a caller left off. One copy, so the three endpoints cannot
    /// drift into generating different loops from identical URLs.
    ///
    /// <para>Every parameter is nullable so that "the caller omitted this" stays distinguishable
    /// from "the caller sent zero". Purity is why it matters: 0 is a real setting at the bottom of
    /// the slider, and reading it as absent used to substitute 90 — which rotates the progression
    /// through <c>DelayTheHomeChord</c>. On an export that only meant a download differing from the
    /// screen; on a hum take it meant filing the recording under chords the singer never heard.</para>
    /// </summary>
    private static ModalMelodyRequest FromQuery(
        int? tonicPitchClass,
        ScaleMode? mode,
        string? progressionId,
        double? bpm,
        MelodyStyle? style,
        int? seed,
        double? targetPurity)
        => new(
            TonicPitchClass: tonicPitchClass ?? 0,
            Mode: mode ?? ScaleMode.Ionian,
            ProgressionId: string.IsNullOrEmpty(progressionId) ? "pop-axis" : progressionId,
            Bpm: bpm ?? 100.0,
            Style: style ?? MelodyStyle.Lyrical,
            Seed: seed ?? 42,
            TargetPurity: targetPurity ?? 90.0);
}
