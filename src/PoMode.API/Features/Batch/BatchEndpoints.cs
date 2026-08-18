using System.IO.Compression;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.MidiExport;
using PoMode.API.Features.MusicXml;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Batch;

public static class BatchEndpoints
{
    private const int MaxTracks = 20;

    public static IEndpointRouteBuilder MapBatch(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis/batch");
        group.AddEndpointFilter<JobIdEndpointFilter>();

        // Anonymous like the single-file upload: RadzenUpload posts the files itself and cannot
        // attach the FakeAuth headers.
        group.MapPost("", async Task<Results<Ok<BatchStatusDto>, BadRequest<string>>> (
            HttpRequest request, AnalysisIntake intake, BatchStore batches, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return TypedResults.BadRequest("Expected a multipart form upload.");
            }
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (InvalidDataException)
            {
                return TypedResults.BadRequest("No files uploaded.");
            }
            if (form.Files.Count == 0)
            {
                return TypedResults.BadRequest("No files uploaded.");
            }
            if (form.Files.Count > MaxTracks)
            {
                return TypedResults.BadRequest($"A batch is limited to {MaxTracks} tracks.");
            }

            // Validate everything up front: a batch either starts whole or not at all. The throw
            // arm makes an unhandled UploadRejection fail closed instead of accepting the file.
            foreach (var file in form.Files)
            {
                if (await AudioFormatValidator.ValidateAsync(file, ct) is { } rejection)
                {
                    return TypedResults.BadRequest(rejection switch
                    {
                        UploadRejection.TooLarge => $"'{file.FileName}' exceeds the 100 MB limit.",
                        UploadRejection.UnsupportedFormat => $"'{file.FileName}' is not a supported .mp3 or .wav file.",
                        _ => throw new ArgumentOutOfRangeException(nameof(rejection), rejection, "Unhandled upload rejection."),
                    });
                }
            }

            var tracks = new List<BatchTrack>();
            var statuses = new List<BatchTrackStatus>();
            foreach (var file in form.Files)
            {
                await using var stream = file.OpenReadStream();
                var state = await intake.StartAsync(file.FileName, stream, clientCanInfer: false, ct);
                tracks.Add(new BatchTrack(state.JobId, file.FileName));
                statuses.Add(new BatchTrackStatus(state.JobId, file.FileName, state.Stage, state.Progress, state.Error));
            }
            var manifest = await batches.CreateAsync(tracks, ct);
            return TypedResults.Ok(new BatchStatusDto(manifest.BatchId, statuses));
        }).DisableAntiforgery();

        group.MapGet("/{batchId}", async Task<Results<Ok<BatchStatusDto>, NotFound>> (
            string batchId, BatchStore batches, JobStore store, CancellationToken ct) =>
        {
            var manifest = await batches.LoadAsync(batchId, ct);
            if (manifest is null)
            {
                return TypedResults.NotFound();
            }
            var statuses = new List<BatchTrackStatus>();
            foreach (var track in manifest.Tracks)
            {
                var state = await store.LoadAsync(track.JobId, ct);
                statuses.Add(state is null
                    ? new BatchTrackStatus(track.JobId, track.FileName, JobStage.Failed, 0, "Job no longer exists.")
                    : new BatchTrackStatus(track.JobId, track.FileName, state.Stage, state.Progress, state.Error));
            }
            return TypedResults.Ok(new BatchStatusDto(manifest.BatchId, statuses));
        });

        // 409 until every track is terminal; the archive then contains the artifacts of each
        // completed track (JSON + MIDI + MusicXML) and an error.txt for each failed one.
        group.MapGet("/{batchId}/zip", async Task<Results<FileStreamHttpResult, NotFound, Conflict<string>>> (
            string batchId, BatchStore batches, JobStore store, CancellationToken ct) =>
        {
            var manifest = await batches.LoadAsync(batchId, ct);
            if (manifest is null)
            {
                return TypedResults.NotFound();
            }

            var states = new List<(BatchTrack Track, JobState? State)>();
            foreach (var track in manifest.Tracks)
            {
                states.Add((track, await store.LoadAsync(track.JobId, ct)));
            }
            if (states.Any(s => s.State is not null && !s.State.Stage.IsTerminal()))
            {
                return TypedResults.Conflict("The batch is still processing — poll the batch status until every track finishes.");
            }

            // Not `using`: the file result takes ownership and disposes the stream after the
            // response — a second ToArray copy of the whole archive would be pure waste.
            var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (var index = 0; index < states.Count; index++)
                {
                    var (track, state) = states[index];
                    var folder = $"{index + 1:00}-{SafeName(track.FileName)}";
                    if (state is null || state.Stage != JobStage.Complete)
                    {
                        await WriteEntryAsync(archive, $"{folder}/error.txt",
                            System.Text.Encoding.UTF8.GetBytes(state?.Error ?? "The job did not complete."), ct);
                        continue;
                    }
                    var result = await store.ReadArtifactAsync<ModalResult>(track.JobId, "result.json", ct);
                    foreach (var artifact in (string[])["result.json", "notes.json", "chords.json"])
                    {
                        if (await store.ReadArtifactBytesAsync(track.JobId, artifact, ct) is { } bytes)
                        {
                            await WriteEntryAsync(archive, $"{folder}/{artifact}", bytes, ct);
                        }
                    }
                    if (result is not null)
                    {
                        var notes = await store.ReadArtifactListAsync<NoteEvent>(track.JobId, "notes.json", ct);
                        var chords = await store.ReadArtifactListAsync<ChordSpan>(track.JobId, "chords.json", ct);
                        await WriteEntryAsync(archive, $"{folder}/{folder}.mid",
                            MidiFileBuilder.Build(notes, chords, result), ct);
                        await WriteEntryAsync(archive, $"{folder}/{folder}.musicxml",
                            System.Text.Encoding.UTF8.GetBytes(MusicXmlBuilder.Build(
                                notes, chords, result, Path.GetFileNameWithoutExtension(track.FileName))), ct);
                    }
                }
            }
            buffer.Position = 0;
            return TypedResults.Stream(buffer, "application/zip", $"pomode-batch-{batchId}.zip");
        });

        return app;
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] content, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await stream.WriteAsync(content, ct);
    }

    private static string SafeName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var cleaned = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "track" : cleaned[..Math.Min(cleaned.Length, 40)];
    }
}
