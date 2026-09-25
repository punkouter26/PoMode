using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.ModalMelodies;
using PoMode.API.Features.SongStatistics;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// Races every Ollama model installed on this machine through the interpreter's real prompts and
/// rewrites <c>test-reports/interpreter-report.html</c>, the way <see cref="ModelAccuracyReportTests"/>
/// does for the audio models: so the preferred model is chosen on numbers, not on reputation.
///
/// <para>Each model writes the summary, answers the three starter questions, and is asked one question
/// the measurements cannot answer. A task passes when the reply fits the schema, ends before the
/// length cap, and contains no figure the measurements lack (<see cref="GroundingCheck"/>) — and, for
/// the unanswerable question, when it declines. First attempts only: the selector's corrected retry
/// would hide exactly the failures this is here to count.</para>
///
/// <para>Opt-in and slow (minutes per model on a CPU):
/// <c>POMODE_LLM_REPORT=1 dotnet test tests/PoMode.Integration --filter InterpreterReport</c></para>
/// </summary>
[Trait("Category", "Slow")]
public sealed class InterpreterReportTests
{
    private const string OptInVariable = "POMODE_LLM_REPORT";

    private static readonly string[] Questions =
    [
        "Why this mode and not its relative minor?",
        "What makes this hard to sing?",
        "How does the melody sit against the chords?",
    ];

    private const string Unanswerable = "What are the lyrics about, and who sang it?";

    private sealed record TaskRow(
        string Task, bool Fits, IReadOnlyList<string> Unsupported, bool DeclinedCorrectly,
        long FirstTokenMs, long TotalMs, string Error)
    {
        public bool Passed => Fits && Unsupported.Count == 0 && DeclinedCorrectly && Error.Length == 0;
    }

    private sealed record ModelRow(string Model, List<TaskRow> Tasks)
    {
        public int Passed => Tasks.Count(task => task.Passed);
        public double MeanSeconds => Tasks.Average(task => task.TotalMs) / 1000.0;
        public double MeanFirstTokenSeconds => Tasks.Where(t => t.FirstTokenMs > 0).Select(t => t.FirstTokenMs).DefaultIfEmpty(0).Average() / 1000.0;
    }

    [Fact]
    public async Task Every_installed_ollama_model_is_scored_on_the_real_prompts()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptInVariable)))
        {
            return;
        }

        var http = new ServiceCollection().AddHttpClient().BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();
        var models = await InstalledModelsAsync(http);
        Assert.NotEmpty(models); // opted in with no Ollama or no model: say so rather than write an empty report

        var stats = DemoStats();
        var rows = new List<ModelRow>();
        foreach (var model in models)
        {
            var interpreter = new OllamaSongInterpreter(
                new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Llm:Ollama:Model"] = model }).Build(),
                http, NullLogger<OllamaSongInterpreter>.Instance);

            var tasks = new List<TaskRow> { await RunAsync(interpreter, stats, null) };
            foreach (var question in Questions.Append(Unanswerable))
            {
                tasks.Add(await RunAsync(interpreter, stats, question));
            }
            rows.Add(new ModelRow(model, tasks));
        }

        var winner = rows.OrderByDescending(row => row.Passed).ThenBy(row => row.MeanSeconds).First();
        var path = WriteReport(stats, rows, winner);
        Console.WriteLine($"Interpreter report written to: {path}");

        // Same rule as the audio report: the model the app prefers must be the one that scored best,
        // whenever it is installed to be compared. If this fails, change PreferredModel.
        if (rows.FirstOrDefault(row => row.Model.Split(':')[0] == OllamaSongInterpreter.PreferredModel) is { } preferred)
        {
            Assert.True(preferred.Passed >= winner.Passed,
                $"{OllamaSongInterpreter.PreferredModel} passed {preferred.Passed}/{preferred.Tasks.Count} tasks but "
                + $"{winner.Model} passed {winner.Passed}. Change OllamaSongInterpreter.PreferredModel.");
        }
    }

    private static async Task<TaskRow> RunAsync(OllamaSongInterpreter interpreter, SongStats stats, string? question)
    {
        var prompt = question is null ? InterpretationPrompt.For(stats) : QuestionPrompt.For(stats, null, question);
        var reader = new ReplyReader();
        var clock = Stopwatch.StartNew();
        long firstToken = 0;
        var error = "";
        try
        {
            await foreach (var chunk in interpreter.ReplyAsync(new InterpretationRequest(stats, question, prompt), CancellationToken.None))
            {
                firstToken = firstToken == 0 ? clock.ElapsedMilliseconds : firstToken;
                reader.Push(chunk);
            }
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
        }

        var label = question ?? "Summarize this song";
        var main = question is null ? InterpretationPrompt.PlainField : QuestionPrompt.AnswerField;
        var fits = !string.IsNullOrWhiteSpace(reader[main])
            && (question is not null || !string.IsNullOrWhiteSpace(reader[InterpretationPrompt.TheoryField]))
            && (question is null || bool.TryParse(reader[QuestionPrompt.InDataField], out _));
        var prose = $"{reader[main]}\n{reader[InterpretationPrompt.TheoryField]}";
        var declined = question != Unanswerable
            || (bool.TryParse(reader[QuestionPrompt.InDataField], out var inData) && !inData);
        return new TaskRow(label, fits, GroundingCheck.Unsupported(prose, prompt.Shown), declined,
            firstToken, clock.ElapsedMilliseconds, error);
    }

    /// <summary>The demo's F Lydian vamp, analysed for real: realistic statistics, identical every run.</summary>
    private static SongStats DemoStats()
    {
        var generator = new ModalMelodyGenerator();
        var notes = new List<NoteEvent>();
        var chords = new List<ChordSpan>();
        var offset = 0.0;
        for (var pass = 0; pass < 4; pass++)
        {
            var generated = generator.Generate(new ModalMelodyRequest(0, ScaleMode.Lydian, "lydian-space", 84.0, Seed: 7 + pass));
            var shift = offset;
            notes.AddRange(generated.MelodyNotes.Select(note => note with { StartSec = note.StartSec + shift }));
            chords.AddRange(generated.Chords.Select(chord =>
                chord with { StartSec = chord.StartSec + shift, EndSec = chord.EndSec + shift }));
            offset += generated.Chords[^1].EndSec;
        }

        var result = ModalAnalysisEngine.Analyze(notes, chords, 84.0, tempoEstimated: false);
        return SongStatsBuilder.Build(
            VisualizationBuilder.Build(notes, chords, result), chords, result, new BeatGridDto(84.0, 0.0, 1.0));
    }

    private static async Task<List<string>> InstalledModelsAsync(IHttpClientFactory http)
    {
        try
        {
            using var client = http.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync("http://localhost:11434/api/tags"));
            return [.. document.RootElement.GetProperty("models").EnumerateArray()
                .Select(model => model.GetProperty("name").GetString()!)
                .Order(StringComparer.Ordinal)];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    private static string WriteReport(SongStats stats, List<ModelRow> rows, ModelRow winner)
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "test-reports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "interpreter-report.html");

        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <title>PoMode Interpreter Report</title>
            <style>
            body { font-family: system-ui, sans-serif; margin: 2rem auto; max-width: 64rem; color: #1c1c28; }
            h1 { font-size: 1.4rem; } h2 { font-size: 1.1rem; margin-top: 2rem; }
            table { border-collapse: collapse; width: 100%; margin-top: 0.5rem; }
            th, td { border: 1px solid #d5d5e0; padding: 0.4rem 0.6rem; text-align: left; font-size: 0.9rem; vertical-align: top; }
            th { background: #f2f2f7; }
            .best { background: #e8f7ec; font-weight: 600; }
            .fail { color: #a3212a; }
            .muted, .note { color: #70708a; font-size: 0.85rem; }
            </style></head><body>
            <h1>PoMode interpreter report</h1>
            """);
        html.Append(CultureInfo.InvariantCulture, $"""
            <p class="muted">Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}. Measurements: the demo's
            {WebUtility.HtmlEncode(stats.TonicName)} {stats.PrimaryMode} vamp, analysed. Each model writes the summary,
            answers the three starter questions, and is asked one question the data cannot answer. A task passes
            when the reply fits the schema, finishes under the length cap, adds no figure the measurements lack,
            and, for the last question, declines. First attempts only.</p>
            """);

        html.Append("<h2>Models</h2><table><tr><th>Model</th><th>Tasks passed</th><th>Mean first token</th><th>Mean reply time</th></tr>");
        foreach (var row in rows)
        {
            var preferred = row.Model.Split(':')[0] == OllamaSongInterpreter.PreferredModel ? " (preferred)" : "";
            html.Append(CultureInfo.InvariantCulture, $"""
                <tr class="{(row == winner ? "best" : "")}"><td>{WebUtility.HtmlEncode(row.Model)}{preferred}</td>
                <td>{row.Passed}/{row.Tasks.Count}</td><td>{row.MeanFirstTokenSeconds:0.0}s</td><td>{row.MeanSeconds:0.0}s</td></tr>
                """);
        }
        html.Append("</table>");

        html.Append("<h2>Tasks</h2><table><tr><th>Model</th><th>Task</th><th>Schema</th><th>Invented figures</th><th>Declined when it should</th><th>Time</th><th>Error</th></tr>");
        foreach (var row in rows)
        {
            foreach (var task in row.Tasks)
            {
                var invented = task.Unsupported.Count == 0 ? "none" : WebUtility.HtmlEncode(string.Join(", ", task.Unsupported));
                html.Append(CultureInfo.InvariantCulture, $"""
                    <tr><td>{WebUtility.HtmlEncode(row.Model)}</td><td>{WebUtility.HtmlEncode(task.Task)}</td>
                    <td class="{(task.Fits ? "" : "fail")}">{(task.Fits ? "ok" : "broken")}</td>
                    <td class="{(task.Unsupported.Count == 0 ? "" : "fail")}">{invented}</td>
                    <td class="{(task.DeclinedCorrectly ? "" : "fail")}">{(task.DeclinedCorrectly ? "yes" : "no")}</td>
                    <td>{task.TotalMs / 1000.0:0.0}s</td><td class="fail">{WebUtility.HtmlEncode(task.Error)}</td></tr>
                    """);
            }
        }
        html.Append("</table><p class=\"note\">Whole numbers up to 12 are not checked: scale degrees, intervals and small counts are how theory is spoken.</p></body></html>");
        File.WriteAllText(path, html.ToString());
        return path;
    }
}
