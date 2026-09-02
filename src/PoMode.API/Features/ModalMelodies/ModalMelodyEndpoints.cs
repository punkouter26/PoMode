using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.MidiExport;
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
            [FromQuery] int tonicPitchClass,
            [FromQuery] ScaleMode mode,
            [FromQuery] string progressionId,
            [FromQuery] double bpm,
            [FromQuery] MelodyStyle style,
            [FromQuery] int seed,
            [FromQuery] double targetPurity,
            ModalMelodyGenerator generator) =>
        {
            var request = new ModalMelodyRequest(
                TonicPitchClass: tonicPitchClass,
                Mode: mode,
                ProgressionId: string.IsNullOrEmpty(progressionId) ? "pop-axis" : progressionId,
                Bpm: bpm > 0 ? bpm : 100.0,
                Style: style,
                Seed: seed != 0 ? seed : 42,
                TargetPurity: targetPurity > 0 ? targetPurity : 90.0);

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
            [FromQuery] int tonicPitchClass,
            [FromQuery] ScaleMode mode,
            [FromQuery] string progressionId,
            [FromQuery] double bpm,
            [FromQuery] MelodyStyle style,
            [FromQuery] int seed,
            [FromQuery] double targetPurity,
            ModalMelodyGenerator generator) =>
        {
            var request = new ModalMelodyRequest(
                TonicPitchClass: tonicPitchClass,
                Mode: mode,
                ProgressionId: string.IsNullOrEmpty(progressionId) ? "pop-axis" : progressionId,
                Bpm: bpm > 0 ? bpm : 100.0,
                Style: style,
                Seed: seed != 0 ? seed : 42,
                TargetPurity: targetPurity > 0 ? targetPurity : 90.0);

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
                ct: ct);

            return TypedResults.Ok(state.ToDto());
        })
        .WithName("AnalyzeModalMelodyInAnalyzer")
        .WithSummary("Synthesizes the melody and chords into WAV audio and queues an end-to-end analysis job in the Song Analyzer.");

        return app;
    }
}
