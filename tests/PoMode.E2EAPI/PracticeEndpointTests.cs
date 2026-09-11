using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

/// <summary>
/// The practice endpoints over HTTP. No job store and no audio: an exercise is a pure function of six
/// query values and an attempt is a note list, which is the whole reason this feature persists
/// nothing.
/// </summary>
public sealed class PracticeEndpointTests
{
    private static WebApplicationFactory<Program> Factory() => new AuthedFactory();

    private static string ExerciseUrl(
        ModeExerciseKind kind = ModeExerciseKind.ScaleRun,
        ScaleMode mode = ScaleMode.Dorian,
        int tonic = 2,
        double bpm = 72,
        int seed = 42,
        int octave = 4)
        => $"api/practice/exercise?kind={kind}&mode={mode}&tonicPitchClass={tonic}"
            + $"&bpm={bpm.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&seed={seed}&octave={octave}";

    [Fact]
    public async Task An_exercise_carries_its_phrase_and_the_words_that_explain_it()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var exercise = await client.GetFromJsonAsync<ModeExerciseDto>(ExerciseUrl());

        Assert.NotNull(exercise);
        Assert.NotEmpty(exercise.TargetNotes);
        Assert.Equal("D", exercise.TonicName);
        Assert.Equal(ScaleMode.Dorian, exercise.Mode);
        // The sentences are written server-side, like every other musical statement in this app.
        Assert.NotEmpty(exercise.Instruction);
        Assert.NotEmpty(exercise.Title);
        // Dorian's natural 6 is what the page highlights, so the server has to name it.
        Assert.Contains("6", exercise.CharacteristicDegrees);
    }

    [Fact]
    public async Task The_same_url_is_the_same_phrase()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var first = await client.GetFromJsonAsync<ModeExerciseDto>(ExerciseUrl(seed: 7));
        var second = await client.GetFromJsonAsync<ModeExerciseDto>(ExerciseUrl(seed: 7));

        Assert.Equal(
            first!.TargetNotes.Select(note => note.MidiPitch),
            second!.TargetNotes.Select(note => note.MidiPitch));
    }

    [Fact]
    public async Task An_attempt_is_graded_against_the_phrase_the_server_issued()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var exercise = await client.GetFromJsonAsync<ModeExerciseDto>(ExerciseUrl(bpm: 60));
        // Sung back exactly. The attempt carries only the six values, never the notes, so this also
        // proves the server rebuilt the identical phrase to grade against.
        var attempt = new ExerciseAttemptRequest(
            exercise!.Kind, exercise.Mode, exercise.TonicPitchClass,
            exercise.Bpm, exercise.Seed, exercise.Octave, exercise.TargetNotes);

        var response = await client.PostAsJsonAsync("api/practice/attempt", attempt);
        response.EnsureSuccessStatusCode();
        var score = await response.Content.ReadFromJsonAsync<ExerciseScoreDto>();

        Assert.NotNull(score);
        Assert.Equal(100, score.Score);
        Assert.Equal(exercise.TargetNotes.Count, score.NotesHit);
        Assert.NotEmpty(score.Verdict);
    }

    [Fact]
    public async Task Notes_a_browser_could_not_have_heard_are_refused()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // Well outside a piano, let alone a voice. The same validator the client-delegated pitch
        // results go through: "the browser detected it" is not a reason to trust the numbers.
        var attempt = new ExerciseAttemptRequest(
            ModeExerciseKind.ScaleRun, ScaleMode.Dorian, 2, 72, 42, 4,
            [new NoteEvent(500, 0.0, 1.0, 90)]);

        var response = await client.PostAsJsonAsync("api/practice/attempt", attempt);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Grading_requires_an_authenticated_caller()
    {
        // A plain factory, so no FakeAuth headers are attached — matching WriteEndpointAuthTests.
        // Reading an exercise stays open (it is public music theory); posting a take does not.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("api/practice/attempt", new ExerciseAttemptRequest(
            ModeExerciseKind.ScaleRun, ScaleMode.Dorian, 2, 72, 42, 4, []));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
