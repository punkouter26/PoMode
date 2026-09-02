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

    /// <summary><paramref name="Rank"/> is <see cref="ExecutionPlanner.EffectiveRank"/> for this
    /// executor: the lowest-ranked available one is what a real job actually runs.</summary>
    private sealed record PitchRow(string Name, string Kind, bool Available, int Rank,
        int NotesFound, double Precision, double Recall, double F1, long Milliseconds);

    private sealed record ChordRow(string Name, string Kind, int Rank,
        int ChordsFound, double Accuracy, long Milliseconds);

    // ---- Real-song section ----

    /// <summary>
    /// A real recording to run the free executors over. There is no ground truth for it, so this
    /// half of the report measures behaviour and cross-model agreement rather than accuracy.
    /// Override with the <c>POMODE_REAL_SONG</c> environment variable; when the file is missing
    /// (CI, another machine) the section simply reports that and the test still passes.
    /// </summary>
    private static string RealSongPath =>
        Environment.GetEnvironmentVariable("POMODE_REAL_SONG")
        ?? @"C:\Users\punko\OneDrive\VAULT\_SOUND\2023_SlowJen.wav";

    private sealed record RealPitchRow(
        string Name, string Kind, int NotesFound, double NotesPerSecond,
        string PitchRange, string DetectedKey, long Milliseconds);

    private sealed record RealChordRow(
        string Name, string Kind, int ChordsFound, int DistinctChords,
        double MeanChordSeconds, long Milliseconds);

    private sealed record RealSongReport(
        bool Present, string FileName, double DurationSec, int SampleRate, int Channels,
        List<RealPitchRow> Pitch, List<RealChordRow> Chords,
        double PitchAgreement, double ChordAgreement);

    /// <summary>Set to any value to run the report. Off by default, see the note on the method.</summary>
    private const string OptInVariable = "POMODE_MODEL_REPORT";

    /// <summary>
    /// This is a reporting tool rather than a test: it races every free executor against a known-truth
    /// sample and rewrites test-reports/model-accuracy.html. It asserts nothing about the change you
    /// are making, and it is the slowest thing in this suite, so it stays off unless asked for:
    ///
    /// <code>POMODE_MODEL_REPORT=1 dotnet test tests/PoMode.Integration</code>
    /// </summary>
    [Fact]
    public async Task Every_free_model_is_scored_against_ground_truth_and_reported_as_html()
    {
        // Returns rather than skipping: this xUnit version has no runtime skip, and the report
        // asserts nothing, so doing no work is the honest no-op.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OptInVariable)))
        {
            return;
        }

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
            pitchRows.Add(new PitchRow(
                onnx.Name, "model", Available: false, ExecutionPlanner.EffectiveRank(onnx), 0, 0, 0, 0, 0));
        }
        pitchRows.Add(await ScorePitchAsync(new YinPitchTracker(), "method", inputPath));

        // ---- 3. Run every free chord recognizer on the mix ----
        var chordRows = new List<ChordRow>
        {
            await ScoreChordsAsync(new ChromaChordRecognizer(), "method", inputPath),
            await ScoreChordsAsync(new ViterbiChordRecognizer(), "method", inputPath),
        };

        // ---- 4. Run the same executors over a real recording (no truth: agreement, not accuracy) ----
        var realSong = await AnalyseRealSongAsync(registry);

        // ---- 5. Deploy the HTML report ----
        var reportPath = WriteReport(inputFormat, pitchRows, chordRows, realSong);
        Console.WriteLine($"Model accuracy report written to: {reportPath}");

        // ---- 6. The report is the deliverable; these are the guarantees it must keep ----
        Assert.True(File.Exists(reportPath));
        var yin = pitchRows.Single(r => r.Name == nameof(YinPitchTracker));
        Assert.True(yin.F1 >= 0.5, $"YIN F1 was {yin.F1:0.00} on a clean sine melody");
        Assert.All(chordRows, row =>
            Assert.True(row.Accuracy >= 0.5, $"{row.Name} chord accuracy was {row.Accuracy:P0}"));

        // The default must BE the best model, not merely be ranked first. Ranking is a hand-written
        // ordering and the scores are measured, so without this they can drift apart silently — a
        // new executor could win the bake-off and never actually run. If this fails, either the
        // registration order in Program.cs is wrong or the winner changed: re-rank, don't relax it.
        AssertDefaultIsTheWinner(
            StageNames.PitchTracking,
            [.. pitchRows.Where(r => r.Available).Select(r => (r.Name, r.Rank, Score: r.F1))]);
        AssertDefaultIsTheWinner(
            StageNames.ChordDetecting,
            [.. chordRows.Select(r => (r.Name, r.Rank, Score: r.Accuracy))]);
    }

    /// <summary>
    /// Fails when the executor a real job would run for <paramref name="stage"/> is not the one
    /// that scored highest. A tie is fine — several executors can be equally good, and then any of
    /// them is a defensible default — but a lower-ranked executor scoring strictly higher means
    /// the app is knowingly running the worse model.
    /// </summary>
    private static void AssertDefaultIsTheWinner(
        string stage, IReadOnlyList<(string Name, int Rank, double Score)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var chosen = rows.MinBy(r => r.Rank);
        var best = rows.MaxBy(r => r.Score);
        Assert.True(
            chosen.Score >= best.Score,
            $"{stage} defaults to {chosen.Name} (score {chosen.Score:0.00}) but {best.Name} scored "
            + $"{best.Score:0.00}. The planner is running the worse model — fix the ranking or the model.");
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
        return new PitchRow(tracker.Name, kind, Available: true, ExecutionPlanner.EffectiveRank(tracker),
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
        return new ChordRow(recognizer.Name, kind, ExecutionPlanner.EffectiveRank(recognizer),
            spans.Count, correct / (double)samples, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Runs every free executor over a real recording. Nothing here is scored against a truth —
    /// there isn't one — so the honest signals are: what each model actually produced, how long it
    /// took on real audio, what key the app would report, and how far the two independent models
    /// agree with each other. Two models agreeing is weak evidence they are right; two models
    /// disagreeing is strong evidence at least one is wrong, and that is worth seeing.
    /// </summary>
    private async Task<RealSongReport> AnalyseRealSongAsync(ModelRegistry registry)
    {
        var path = RealSongPath;
        if (!File.Exists(path))
        {
            Console.WriteLine($"Real-song section skipped: '{path}' not present on this machine.");
            return new RealSongReport(Present: false, Path.GetFileName(path), 0, 0, 0, [], [], 0, 0);
        }

        // A job dir of its own: the synthetic run wrote a vocals.wav into _dir, and the pitch
        // trackers prefer that file — reusing the directory would silently analyse the wrong audio.
        var jobDir = Path.Combine(_dir, "real");
        Directory.CreateDirectory(jobDir);
        var context = new StageContext("real", jobDir, path);

        var decoded = PoMode.API.Features.Audio.AudioDecoder.Decode(path);
        var duration = decoded.DurationSeconds;

        var pitchRows = new List<RealPitchRow>();
        var noteSets = new List<IReadOnlyList<NoteEvent>>();
        var trackers = new List<(IPitchTracker Tracker, string Kind)> { (new YinPitchTracker(), "method") };
        if (registry.IsDownloaded(ModelCatalog.BasicPitch))
        {
            trackers.Insert(0, (new OnnxPitchTracker(registry, NullLogger<OnnxPitchTracker>.Instance), "model"));
        }

        // One chord run feeds the modal engine for every tracker, so the reported keys differ only
        // by the melody the tracker heard — the variable actually under test here.
        var chordStopwatch = Stopwatch.StartNew();
        var chromaChords = await new ChromaChordRecognizer().RecognizeAsync(context, CancellationToken.None);
        chordStopwatch.Stop();
        var viterbiStopwatch = Stopwatch.StartNew();
        var viterbiChords = await new ViterbiChordRecognizer().RecognizeAsync(context, CancellationToken.None);
        viterbiStopwatch.Stop();

        foreach (var (tracker, kind) in trackers)
        {
            var stopwatch = Stopwatch.StartNew();
            var notes = await tracker.TrackAsync(context, CancellationToken.None);
            stopwatch.Stop();
            noteSets.Add(notes);

            var modal = PoMode.API.Features.ModalAnalysis.ModalAnalysisEngine.Analyze(notes, chromaChords);
            var range = notes.Count == 0
                ? "—"
                : $"{PitchLabel(notes.Min(n => n.MidiPitch))}–{PitchLabel(notes.Max(n => n.MidiPitch))}";
            pitchRows.Add(new RealPitchRow(
                tracker.Name, kind, notes.Count,
                duration > 0 ? notes.Count / duration : 0,
                range,
                $"{modal.TonicName} {modal.PrimaryMode?.ToString() ?? "(unclear)"}",
                stopwatch.ElapsedMilliseconds));
        }

        var chordRows = new List<RealChordRow>
        {
            RealChordRowFor(new ChromaChordRecognizer().Name, chromaChords, chordStopwatch.ElapsedMilliseconds),
            RealChordRowFor(new ViterbiChordRecognizer().Name, viterbiChords, viterbiStopwatch.ElapsedMilliseconds),
        };

        return new RealSongReport(
            Present: true,
            Path.GetFileName(path),
            duration,
            decoded.SampleRate,
            decoded.Channels,
            pitchRows,
            chordRows,
            noteSets.Count == 2 ? NoteAgreement(noteSets[0], noteSets[1]) : double.NaN,
            ChordAgreement(chromaChords, viterbiChords, duration));
    }

    private static RealChordRow RealChordRowFor(string name, IReadOnlyList<ChordSpan> spans, long milliseconds)
        => new(name, "method", spans.Count,
            spans.Select(s => s.Symbol).Distinct().Count(),
            spans.Count == 0 ? 0 : spans.Average(s => s.EndSec - s.StartSec),
            milliseconds);

    private static string PitchLabel(int midi) => $"{ScaleModes.NoteName(midi)}{(midi / 12) - 1}";

    /// <summary>
    /// F1 overlap between two note lists under the same rule the truth scoring uses: same pitch,
    /// onset within 0.25 s, each prediction consumed once.
    /// </summary>
    private static double NoteAgreement(IReadOnlyList<NoteEvent> left, IReadOnlyList<NoteEvent> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }
        var consumed = new bool[right.Count];
        var hits = 0;
        foreach (var note in left)
        {
            for (var i = 0; i < right.Count; i++)
            {
                if (!consumed[i] && right[i].MidiPitch == note.MidiPitch
                    && Math.Abs(right[i].StartSec - note.StartSec) <= 0.25)
                {
                    consumed[i] = true;
                    hits++;
                    break;
                }
            }
        }
        var precision = hits / (double)right.Count;
        var recall = hits / (double)left.Count;
        return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    /// <summary>Share of 100 ms timeline samples where both recognizers name the same chord.</summary>
    private static double ChordAgreement(
        IReadOnlyList<ChordSpan> left, IReadOnlyList<ChordSpan> right, double durationSec)
    {
        var samples = 0;
        var same = 0;
        for (var t = 0.05; t < durationSec; t += 0.1)
        {
            samples++;
            var l = TimelineSearch.IndexCovering(left, t, s => s.StartSec, s => s.EndSec);
            var r = TimelineSearch.IndexCovering(right, t, s => s.StartSec, s => s.EndSec);
            var leftSymbol = l is null ? "N" : left[l.Value].Symbol;
            var rightSymbol = r is null ? "N" : right[r.Value].Symbol;
            if (leftSymbol == rightSymbol)
            {
                same++;
            }
        }
        return samples == 0 ? 0 : same / (double)samples;
    }

    private static string WriteReport(
        string inputFormat, List<PitchRow> pitchRows, List<ChordRow> chordRows, RealSongReport realSong)
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
            "<th>Executor</th><th>Kind</th><th>Runs by default</th><th>Notes found</th><th>Precision</th><th>Recall</th><th>F1</th><th>Runtime</th></tr>");
        var bestF1 = pitchRows.Where(r => r.Available).Select(r => r.F1).DefaultIfEmpty(0).Max();
        var defaultPitch = pitchRows.Where(r => r.Available).MinBy(r => r.Rank)?.Name;
        foreach (var row in pitchRows)
        {
            if (!row.Available)
            {
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr class=\"muted\"><td>{row.Name}</td><td>{row.Kind}</td><td colspan=\"6\">not available on this machine (model not downloaded)</td></tr>");
                continue;
            }
            var css = row.F1 >= bestF1 && bestF1 > 0 ? " class=\"best\"" : "";
            html.Append(CultureInfo.InvariantCulture,
                $"<tr{css}><td>{row.Name}</td><td>{row.Kind}</td><td>{(row.Name == defaultPitch ? "✓ default" : "")}</td><td>{row.NotesFound}</td><td>{row.Precision:P0}</td><td>{row.Recall:P0}</td><td>{row.F1:0.00}</td><td>{row.Milliseconds} ms</td></tr>");
        }
        html.Append("</table><p class=\"note\">Truth: 8 notes. A prediction scores when the pitch matches " +
            "exactly and the onset is within 0.25s. Trackers read the melody-only vocal stem, as in the real pipeline. " +
            "The test fails if the default row is not also the highest-scoring one.</p>");

        html.Append("<h2>Chords (ChordDetecting)</h2><table><tr>" +
            "<th>Executor</th><th>Kind</th><th>Runs by default</th><th>Chords found</th><th>Frame accuracy</th><th>Runtime</th></tr>");
        var bestAccuracy = chordRows.Select(r => r.Accuracy).DefaultIfEmpty(0).Max();
        var defaultChord = chordRows.MinBy(r => r.Rank)?.Name;
        foreach (var row in chordRows)
        {
            var css = row.Accuracy >= bestAccuracy && bestAccuracy > 0 ? " class=\"best\"" : "";
            html.Append(CultureInfo.InvariantCulture,
                $"<tr{css}><td>{row.Name}</td><td>{row.Kind}</td><td>{(row.Name == defaultChord ? "✓ default" : "")}</td><td>{row.ChordsFound}</td><td>{row.Accuracy:P0}</td><td>{row.Milliseconds} ms</td></tr>");
        }
        html.Append("</table><p class=\"note\">Truth: C · G · Am · F, 2s each. Accuracy is the share of " +
            "100ms timeline samples whose predicted symbol equals the truth. Recognizers read the full mix.</p>");

        // ---- Real recording: no truth exists, so this reports behaviour and agreement ----
        html.Append("<h2>Real recording</h2>");
        if (!realSong.Present)
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<p class=\"note\">No real song on this machine ({realSong.FileName}). Set POMODE_REAL_SONG to a .wav or .mp3 path to include this section.</p>");
        }
        else
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<p>{realSong.FileName} — {realSong.DurationSec:0}s, {realSong.SampleRate} Hz, {realSong.Channels} ch. <strong>There is no ground truth for a real recording</strong>, so nothing below is an accuracy score. These are the measurements the models actually produced, plus how far they agree with each other.</p>");
            html.Append("<p class=\"note\"><strong>Read with this caveat:</strong> stem separation is too slow for this suite, so the pitch trackers here read the <em>full mix</em>, not an isolated vocal. Basic Pitch is polyphonic and copes; YIN is monophonic by design, so this is its worst case and its numbers understate what it does on a real separated stem. The case still matters — the short-clip fast path and Azure mode both skip separation.</p>");

            html.Append("<h3>Melody</h3><table><tr>" +
                "<th>Executor</th><th>Kind</th><th>Notes</th><th>Notes/sec</th><th>Pitch range</th><th>Key it implies</th><th>Runtime</th></tr>");
            foreach (var row in realSong.Pitch)
            {
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{row.Name}</td><td>{row.Kind}</td><td>{row.NotesFound}</td><td>{row.NotesPerSecond:0.0}</td><td>{row.PitchRange}</td><td>{row.DetectedKey}</td><td>{row.Milliseconds} ms</td></tr>");
            }
            html.Append("</table>");

            html.Append("<h3>Chords</h3><table><tr>" +
                "<th>Executor</th><th>Kind</th><th>Chords</th><th>Distinct</th><th>Mean length</th><th>Runtime</th></tr>");
            foreach (var row in realSong.Chords)
            {
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{row.Name}</td><td>{row.Kind}</td><td>{row.ChordsFound}</td><td>{row.DistinctChords}</td><td>{row.MeanChordSeconds:0.0}s</td><td>{row.Milliseconds} ms</td></tr>");
            }
            html.Append("</table>");

            var pitchAgreement = double.IsNaN(realSong.PitchAgreement)
                ? "only one tracker available"
                : realSong.PitchAgreement.ToString("P0", CultureInfo.InvariantCulture);
            html.Append(CultureInfo.InvariantCulture,
                $"<h3>Model agreement</h3><table><tr><th>Pair</th><th>Agreement</th></tr><tr><td>Pitch trackers (same note, onset within 0.25s)</td><td>{pitchAgreement}</td></tr><tr><td>Chord recognizers (100ms timeline samples)</td><td>{realSong.ChordAgreement:P0}</td></tr></table>");
            html.Append("<p class=\"note\">Agreement is not accuracy. Two models agreeing is weak evidence they are both right; two models disagreeing is strong evidence at least one is wrong. Read low agreement as \"go listen to this song and see who is closer\", not as a score.</p>");
        }

        html.Append("<h2>Not compared here</h2><p class=\"note\">" +
            "Separating: OnnxStemSeparator (HTDemucs) needs minutes of runtime and ~5.7 GB of memory per run, " +
            "so it stays out of the routine suite; Replicate/LALAL are paid cloud APIs. " +
            "ModalAnalysis: one deterministic rule engine (ModalAnalysisEngine), no alternatives to race.</p>" +
            "</body></html>");

        File.WriteAllText(reportPath, html.ToString());
        return reportPath;
    }
}
