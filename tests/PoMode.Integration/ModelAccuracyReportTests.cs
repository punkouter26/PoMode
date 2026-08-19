using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.MediaFoundation;
using NAudio.Wave;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// Model bake-off on a synthetic sample song with a known ground truth: an MP3 of a sine melody
/// over a C–G–Am–F triad pad. Every registered free executor for PitchTracking and ChordDetecting
/// runs on it and is scored against the truth; the results are written to
/// <c>test-reports/model-accuracy.html</c> at the repo root on every run, so the report is always
/// as fresh as the last <c>dotnet test tests/PoMode.Integration</c>.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ModelAccuracyReportTests : IDisposable
{
    private const int SampleRate = 44100;
    private const double SongSeconds = 8.0;

    /// <summary>The melody ground truth: one note per second, all chord tones of the pad below.</summary>
    private static readonly (int Midi, double StartSec)[] TruthMelody =
        [(72, 0.0), (76, 1.0), (74, 2.0), (71, 3.0), (69, 4.0), (72, 5.0), (69, 6.0), (77, 7.0)];

    private const double TruthNoteSeconds = 0.9; // 0.1 s gap so every onset is unambiguous

    /// <summary>The chord ground truth: the pad actually rendered under the melody.</summary>
    private static readonly (string Symbol, int[] PadMidis, double StartSec, double EndSec)[] TruthChords =
    [
        ("C", [48, 52, 55], 0.0, 2.0),
        ("G", [55, 59, 62], 2.0, 4.0),
        ("Am", [57, 60, 64], 4.0, 6.0),
        ("F", [53, 57, 60], 6.0, 8.0),
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-accuracy-{Guid.NewGuid():N}");

    public ModelAccuracyReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed record PitchRow(string Name, string Kind, bool Available,
        int NotesFound, double Precision, double Recall, double F1, long Milliseconds);

    private sealed record ChordRow(string Name, string Kind, int ChordsFound, double Accuracy, long Milliseconds);

    [Fact]
    public async Task Every_free_model_is_scored_against_ground_truth_and_reported_as_html()
    {
        // ---- 1. Render the sample song and its melody-only "vocal stem" ----
        var melodyTones = TruthMelody
            .Select(note => (note.Midi, note.StartSec, TruthNoteSeconds, 0.4))
            .ToList();
        var mixTones = melodyTones
            .Concat(TruthChords.SelectMany(chord => chord.PadMidis.Select(
                padMidi => (padMidi, chord.StartSec, chord.EndSec - chord.StartSec, 0.12))))
            .ToList();

        // vocals.wav mimics a perfect separation, which is what the pitch trackers consume in the
        // real pipeline; the full mix is what the chord recognizers consume. TestAudio.MakeSong
        // peak-normalises, which preserves the melody/pad level ratio the scoring depends on.
        var vocalsPath = Path.Combine(_dir, "vocals.wav");
        File.WriteAllBytes(vocalsPath, TestAudio.MakeSong(SongSeconds, melodyTones, SampleRate));
        var mixWavPath = Path.Combine(_dir, "mix.wav");
        File.WriteAllBytes(mixWavPath, TestAudio.MakeSong(SongSeconds, mixTones, SampleRate));

        var (inputPath, inputFormat) = EncodeMp3OrFallBack(mixWavPath);

        // ---- 2. Run every free pitch tracker on the vocal stem ----
        var registry = Registry();
        var pitchRows = new List<PitchRow>();
        var onnx = new OnnxPitchTracker(registry, NullLogger<OnnxPitchTracker>.Instance);
        if (registry.IsDownloaded(ModelCatalog.BasicPitch))
        {
            pitchRows.Add(await ScorePitchAsync(onnx, "model", inputPath));
        }
        else
        {
            pitchRows.Add(new PitchRow(onnx.Name, "model", Available: false, 0, 0, 0, 0, 0));
        }
        pitchRows.Add(await ScorePitchAsync(new YinPitchTracker(), "method", inputPath));

        // ---- 3. Run every free chord recognizer on the mix ----
        var chordRows = new List<ChordRow>
        {
            await ScoreChordsAsync(new ChromaChordRecognizer(), "method", inputPath),
            await ScoreChordsAsync(new ViterbiChordRecognizer(), "method", inputPath),
        };

        // ---- 4. Deploy the HTML report ----
        var reportPath = WriteReport(inputFormat, pitchRows, chordRows);
        Console.WriteLine($"Model accuracy report written to: {reportPath}");

        // ---- 5. Sanity floor, not a leaderboard: the report is the deliverable ----
        Assert.True(File.Exists(reportPath));
        var yin = pitchRows.Single(r => r.Name == nameof(YinPitchTracker));
        Assert.True(yin.F1 >= 0.5, $"YIN F1 was {yin.F1:0.00} on a clean sine melody");
        Assert.All(chordRows, row =>
            Assert.True(row.Accuracy >= 0.5, $"{row.Name} chord accuracy was {row.Accuracy:P0}"));
    }

    /// <summary>MP3 via Windows Media Foundation; on a machine without the encoder the WAV stands
    /// in and the report says so — the comparison itself is identical either way.</summary>
    private (string Path, string Format) EncodeMp3OrFallBack(string wavPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (wavPath, "WAV (MP3 encoding needs Windows Media Foundation)");
        }
        var mp3Path = Path.Combine(_dir, "sample.mp3");
        try
        {
            MediaFoundationApi.Startup();
            using var reader = new WaveFileReader(wavPath);
            MediaFoundationEncoder.EncodeToMp3(reader, mp3Path, 128000);
            return (mp3Path, "MP3 (128 kbps)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MP3 encode unavailable, using WAV: {ex.Message}");
            return (wavPath, $"WAV (MP3 encoder unavailable: {ex.GetType().Name})");
        }
    }

    /// <summary>Points at the running app's own model cache, so the comparison includes the real
    /// Basic Pitch model whenever this machine has downloaded it. The cache is discovered by
    /// globbing the API's bin folder rather than spelling out configuration and TFM, so a .NET
    /// upgrade or a Release build cannot silently flip the report to "model not downloaded".</summary>
    private static ModelRegistry Registry()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        var apiBin = Path.Combine(TestPaths.RepoRoot(), "src", "PoMode.API", "bin");
        var appModels = Directory.Exists(apiBin)
            ? Directory.GetDirectories(apiBin, "models", SearchOption.AllDirectories)
                .FirstOrDefault(dir => File.Exists(Path.Combine(dir, ModelCatalog.BasicPitch.FileName)))
            : null;
        var config = new ConfigurationBuilder();
        if (appModels is not null)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Models:RootPath"] = appModels });
        }
        return new ModelRegistry(
            config.Build(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ModelRegistry>.Instance);
    }

    private async Task<PitchRow> ScorePitchAsync(IPitchTracker tracker, string kind, string inputPath)
    {
        var context = new StageContext("accuracy", _dir, inputPath);
        var stopwatch = Stopwatch.StartNew();
        var notes = await tracker.TrackAsync(context, CancellationToken.None);
        stopwatch.Stop();

        // Greedy onset matching: a predicted note scores when pitch matches exactly and the onset
        // lands within a quarter second of the truth. Octave errors count as misses on purpose.
        var matchedPredictions = new bool[notes.Count];
        var hits = 0;
        foreach (var (midi, startSec) in TruthMelody)
        {
            for (var i = 0; i < notes.Count; i++)
            {
                if (!matchedPredictions[i] && notes[i].MidiPitch == midi
                    && Math.Abs(notes[i].StartSec - startSec) <= 0.25)
                {
                    matchedPredictions[i] = true;
                    hits++;
                    break;
                }
            }
        }
        var precision = notes.Count == 0 ? 0 : hits / (double)notes.Count;
        var recall = hits / (double)TruthMelody.Length;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new PitchRow(tracker.Name, kind, Available: true,
            notes.Count, precision, recall, f1, stopwatch.ElapsedMilliseconds);
    }

    private async Task<ChordRow> ScoreChordsAsync(IChordRecognizer recognizer, string kind, string inputPath)
    {
        var context = new StageContext("accuracy", _dir, inputPath);
        var stopwatch = Stopwatch.StartNew();
        var spans = await recognizer.RecognizeAsync(context, CancellationToken.None);
        stopwatch.Stop();

        // Frame accuracy: sample the timeline every 100 ms and compare symbols, using the app's
        // own half-open covering-span search so the boundary convention cannot drift from it.
        var samples = 0;
        var correct = 0;
        for (var t = 0.05; t < SongSeconds; t += 0.1)
        {
            samples++;
            var truthIndex = TimelineSearch.IndexCovering(TruthChords, t, c => c.StartSec, c => c.EndSec);
            var predictedIndex = TimelineSearch.IndexCovering(spans, t, s => s.StartSec, s => s.EndSec);
            var truth = TruthChords[truthIndex!.Value].Symbol;
            var predicted = predictedIndex is null ? "N" : spans[predictedIndex.Value].Symbol;
            if (predicted == truth)
            {
                correct++;
            }
        }
        return new ChordRow(recognizer.Name, kind, spans.Count,
            correct / (double)samples, stopwatch.ElapsedMilliseconds);
    }

    private static string WriteReport(string inputFormat, List<PitchRow> pitchRows, List<ChordRow> chordRows)
    {
        var reportDir = Path.Combine(TestPaths.RepoRoot(), "test-reports");
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, "model-accuracy.html");

        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <title>PoMode Model Accuracy</title>
            <style>
            body { font-family: system-ui, sans-serif; margin: 2rem auto; max-width: 60rem; color: #1c1c28; }
            h1 { font-size: 1.4rem; } h2 { font-size: 1.1rem; margin-top: 2rem; }
            table { border-collapse: collapse; width: 100%; margin-top: 0.5rem; }
            th, td { border: 1px solid #d5d5e0; padding: 0.4rem 0.6rem; text-align: left; font-size: 0.9rem; }
            th { background: #f2f2f7; }
            .best { background: #e8f7ec; font-weight: 600; }
            .muted { color: #70708a; }
            .note { color: #70708a; font-size: 0.85rem; margin-top: 0.4rem; }
            </style></head><body>
            <h1>PoMode model accuracy report</h1>
            """);
        html.Append(CultureInfo.InvariantCulture,
            $"<p class=\"muted\">Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} by the PoMode.Integration suite.</p>");
        html.Append(CultureInfo.InvariantCulture,
            $"<p>Sample input: {SongSeconds:0}s synthetic song, {inputFormat} — 8-note sine melody (C5 E5 D5 B4 A4 C5 A4 F5) over a C&nbsp;·&nbsp;G&nbsp;·&nbsp;Am&nbsp;·&nbsp;F triad pad, 2s per chord. Ground truth is exact because the song is rendered from it.</p>");

        html.Append("<h2>Melody (PitchTracking)</h2><table><tr>" +
            "<th>Executor</th><th>Kind</th><th>Notes found</th><th>Precision</th><th>Recall</th><th>F1</th><th>Runtime</th></tr>");
        var bestF1 = pitchRows.Where(r => r.Available).Select(r => r.F1).DefaultIfEmpty(0).Max();
        foreach (var row in pitchRows)
        {
            if (!row.Available)
            {
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr class=\"muted\"><td>{row.Name}</td><td>{row.Kind}</td><td colspan=\"5\">not available on this machine (model not downloaded)</td></tr>");
                continue;
            }
            var css = row.F1 >= bestF1 && bestF1 > 0 ? " class=\"best\"" : "";
            html.Append(CultureInfo.InvariantCulture,
                $"<tr{css}><td>{row.Name}</td><td>{row.Kind}</td><td>{row.NotesFound}</td><td>{row.Precision:P0}</td><td>{row.Recall:P0}</td><td>{row.F1:0.00}</td><td>{row.Milliseconds} ms</td></tr>");
        }
        html.Append("</table><p class=\"note\">Truth: 8 notes. A prediction scores when the pitch matches " +
            "exactly and the onset is within 0.25s. Trackers read the melody-only vocal stem, as in the real pipeline.</p>");

        html.Append("<h2>Chords (ChordDetecting)</h2><table><tr>" +
            "<th>Executor</th><th>Kind</th><th>Chords found</th><th>Frame accuracy</th><th>Runtime</th></tr>");
        var bestAccuracy = chordRows.Select(r => r.Accuracy).DefaultIfEmpty(0).Max();
        foreach (var row in chordRows)
        {
            var css = row.Accuracy >= bestAccuracy && bestAccuracy > 0 ? " class=\"best\"" : "";
            html.Append(CultureInfo.InvariantCulture,
                $"<tr{css}><td>{row.Name}</td><td>{row.Kind}</td><td>{row.ChordsFound}</td><td>{row.Accuracy:P0}</td><td>{row.Milliseconds} ms</td></tr>");
        }
        html.Append("</table><p class=\"note\">Truth: C · G · Am · F, 2s each. Accuracy is the share of " +
            "100ms timeline samples whose predicted symbol equals the truth. Recognizers read the full mix.</p>");

        html.Append("<h2>Not compared here</h2><p class=\"note\">" +
            "Separating: OnnxStemSeparator (HTDemucs) needs minutes of runtime and ~5.7 GB of memory per run, " +
            "so it stays out of the routine suite; Replicate/LALAL are paid cloud APIs. " +
            "ModalAnalysis: one deterministic rule engine (ModalAnalysisEngine), no alternatives to race.</p>" +
            "</body></html>");

        File.WriteAllText(reportPath, html.ToString());
        return reportPath;
    }
}
