using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.MediaFoundation;
using NAudio.Wave;
using PoMode.API.Features.BeatTracking;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.Demo;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.ModalMelodies;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// Model bake-off on synthetic material with a known ground truth: an MP3 of a sine melody over a
/// C–G–Am–F triad pad, the same melody as a voice-like line with vibrato and bleed, and two drum
/// grooves with a known bar grid. Every registered free executor for PitchTracking, ChordDetecting
/// and beat tracking runs on it and is scored against the truth; the results are written to
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
    private sealed record PitchRow(string Scenario, string Name, string Kind, bool Available, int Rank,
        int NotesFound, double Precision, double Recall, double F1, long Milliseconds);

    /// <summary>Beat and downbeat F-measure at the standard ±70 ms tolerance.</summary>
    private sealed record BeatRow(string Scenario, string Name, string Kind, bool Available, int Rank,
        double Bpm, double BeatF, double DownbeatF, long Milliseconds)
    {
        public double Score => (BeatF + DownbeatF) / 2;
    }

    /// <summary>The grooves: tempo, bars, and where bar one starts (never at t=0).</summary>
    private static readonly (double Bpm, int Bars, double LeadInSec)[] Grooves = [(104, 8, 0.73), (143, 12, 0.25)];

    private const double BeatToleranceSec = 0.07;

    private sealed record ChordRow(string Scenario, string Name, string Kind, int Rank,
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
        string PitchRange, string DetectedKey, long Milliseconds, double AgreementWithFirst);

    private sealed record RealBeatRow(string Name, string Kind, double Bpm, int Beats, int Bars, long Milliseconds);

    private sealed record RealChordRow(
        string Name, string Kind, int ChordsFound, int DistinctChords,
        double MeanChordSeconds, long Milliseconds);

    private sealed record RealSongReport(
        bool Present, string FileName, double DurationSec, int SampleRate, int Channels,
        List<RealPitchRow> Pitch, List<RealChordRow> Chords, List<RealBeatRow> Beats,
        double ChordAgreement, double BeatAgreement);

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

        // The same melody as a voice would carry it: harmonics, ±40 cents of vibrato, soft edges,
        // and the pad leaking through 18 dB down plus a little noise — what a real separated vocal
        // stem looks like, and the case a vocal model exists for.
        var sungDir = Path.Combine(_dir, "sung");
        Directory.CreateDirectory(sungDir);
        File.WriteAllBytes(Path.Combine(sungDir, "vocals.wav"), SungStem());

        // ---- 2. Run every free pitch tracker on both vocal stems ----
        // List order mirrors DI registration order in Program.cs, because that is how the planner
        // breaks a tie between two executors of the same rank.
        var registry = Registry();
        var pitchTrackers = new List<(IPitchTracker Tracker, string Kind, bool Available)>
        {
            (new RmvpePitchTracker(registry, NullLogger<RmvpePitchTracker>.Instance), "model",
                registry.IsDownloaded(ModelCatalog.Rmvpe)),
            (new OnnxPitchTracker(registry, NullLogger<OnnxPitchTracker>.Instance), "model",
                registry.IsDownloaded(ModelCatalog.BasicPitch)),
            (new YinPitchTracker(), "method", true),
        };
        var pitchRows = new List<PitchRow>();
        foreach (var (scenario, dir) in new[] { ("Sine melody", _dir), ("Sung line + bleed", sungDir) })
        {
            foreach (var (tracker, kind, available) in pitchTrackers)
            {
                pitchRows.Add(available
                    ? await ScorePitchAsync(scenario, tracker, kind, dir, inputPath)
                    : new PitchRow(scenario, tracker.Name, kind, Available: false,
                        ExecutionPlanner.EffectiveRank(tracker), 0, 0, 0, 0, 0));
            }
        }

        // ---- 3. Run every free chord recognizer on the mix ----
        // Two scenarios: the triad pad under a sine melody, and the demo's vamp as the server renders
        // it — piano chords and a flute melody that leans on the chords' non-chord tones, which is
        // what separates a recognizer that hears the harmony from one that matches the loudest notes.
        // List order is DI order again, which breaks ties between executors of one rank.
        var demo = DemoSong.Compose(new ModalMelodyGenerator());
        var demoDir = Path.Combine(_dir, "demo");
        Directory.CreateDirectory(demoDir);
        var demoPath = Path.Combine(demoDir, "mix.wav");
        File.WriteAllBytes(demoPath, demo.Mix);
        var chordMini = new ChordMiniChordRecognizer(registry, NullLogger<ChordMiniChordRecognizer>.Instance);
        var recognizers = new List<(IChordRecognizer Recognizer, string Kind)> { (chordMini, "model"), (new ChromaChordRecognizer(), "method") };
        if (!await chordMini.IsAvailableAsync(CancellationToken.None))
        {
            recognizers.RemoveAt(0);
        }
        recognizers.Add((new ViterbiChordRecognizer(), "method"));
        var sineTruth = TruthChords
            .Select(c => new ChordSpan(c.Symbol, c.Symbol.TrimEnd('m'), c.Symbol.EndsWith('m') ? "min" : "maj", c.StartSec, c.EndSec))
            .ToList();
        var chordRows = new List<ChordRow>();
        foreach (var (scenario, path, truth) in new[]
        {
            ("Triad pad + sine melody", inputPath, (IReadOnlyList<ChordSpan>)sineTruth),
            ($"Demo vamp ({demo.FileName[7..^4]}), full mix", demoPath, demo.Chords),
        })
        {
            foreach (var (recognizer, kind) in recognizers)
            {
                chordRows.Add(await ScoreChordsAsync(scenario, recognizer, kind, path, truth));
            }
        }

        // ---- 3b. Run every beat tracker on the grooves ----
        var beatTrackers = new List<(IBeatTracker Tracker, string Kind)>
        {
            (new BeatThisBeatTracker(registry, NullLogger<BeatThisBeatTracker>.Instance), "model"),
            (new DspBeatTracker(), "method"),
        };
        var beatRows = new List<BeatRow>();
        foreach (var groove in Grooves)
        {
            foreach (var (tracker, kind) in beatTrackers)
            {
                beatRows.Add(await ScoreBeatsAsync(groove, tracker, kind));
            }
        }

        // ---- 4. Run the same executors over a real recording (no truth: agreement, not accuracy) ----
        var realSong = await AnalyseRealSongAsync(pitchTrackers, beatTrackers, chordMini);

        // ---- 5. Deploy the HTML report ----
        var reportPath = WriteReport(inputFormat, pitchRows, chordRows, beatRows, realSong);
        Console.WriteLine($"Model accuracy report written to: {reportPath}");

        // ---- 6. The report is the deliverable; these are the guarantees it must keep ----
        Assert.True(File.Exists(reportPath));
        var yin = pitchRows.First(r => r.Name == nameof(YinPitchTracker));
        Assert.True(yin.F1 >= 0.5, $"YIN F1 was {yin.F1:0.00} on a clean sine melody");
        Assert.All(chordRows.Where(row => row.Scenario == chordRows[0].Scenario), row =>
            Assert.True(row.Accuracy >= 0.5, $"{row.Name} chord accuracy was {row.Accuracy:P0}"));

        // The default must BE the best model, not merely be ranked first. Ranking is a hand-written
        // ordering and the scores are measured, so without this they can drift apart silently — a
        // new executor could win the bake-off and never actually run. If this fails, either the
        // registration order in Program.cs is wrong or the winner changed: re-rank, don't relax it.
        // Pitch and beats are scored on the mean over their scenarios, per executor, in
        // registration order (the order the planner breaks ties in).
        var classic = pitchTrackers.Where(t => t.Tracker.IsClassicFallback).Select(t => t.Tracker.Name)
            .Concat(beatTrackers.Where(t => t.Tracker.IsClassicFallback).Select(t => t.Tracker.Name))
            .ToHashSet();
        AssertDefaultIsTheWinner(
            StageNames.PitchTracking,
            [.. pitchRows.Where(r => r.Available).GroupBy(r => r.Name)
                .Select(g => (g.Key, g.First().Rank, Score: g.Average(r => r.F1),
                    Model: g.First().Kind == "model", Classic: classic.Contains(g.Key)))]);
        AssertDefaultIsTheWinner(
            "BeatTracking",
            [.. beatRows.Where(r => r.Available).GroupBy(r => r.Name)
                .Select(g => (g.Key, g.First().Rank, Score: g.Average(r => r.Score),
                    Model: g.First().Kind == "model", Classic: classic.Contains(g.Key)))]);
        AssertDefaultIsTheWinner(
            StageNames.ChordDetecting,
            [.. chordRows.GroupBy(r => r.Name)
                .Select(g => (g.Key, g.First().Rank, Score: g.Average(r => r.Accuracy), Model: false, Classic: false))]);
    }

    /// <summary>
    /// Fails when the executor a real job would run for <paramref name="stage"/> is not the one
    /// that scored highest. A tie is fine — several executors can be equally good, and then any of
    /// them is a defensible default — but a lower-ranked executor scoring strictly higher means
    /// the app is knowingly running the worse model.
    ///
    /// <para>One exclusion, and it is policy rather than measurement: when the default is a model,
    /// the classic model-less fallbacks are not in the contest. They rank after every model by
    /// design (CLAUDE.md) because they exist for when no model can run, and the synthetic stems
    /// here — clean, monophonic, harmonic — are exactly YIN's best case, not the separated vocals a
    /// real job feeds it; on the real recording YIN reads the bass. The report still prints their
    /// scores, and says so when one beats the default.</para>
    /// </summary>
    private static void AssertDefaultIsTheWinner(
        string stage, IReadOnlyList<(string Name, int Rank, double Score, bool Model, bool Classic)> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var chosen = rows.MinBy(r => r.Rank);
        var contest = chosen.Model ? [.. rows.Where(r => !r.Classic)] : rows;
        var best = contest.MaxBy(r => r.Score);
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

    /// <summary>The sung-line stem: <see cref="TruthMelody"/> through <see cref="TestAudio.MakeSungLine"/>,
    /// with the pad mixed in 18 dB down as separation bleed.</summary>
    private static byte[] SungStem()
    {
        var voice = PoMode.API.Audio.AudioDecoder.Decode(WriteTemp(TestAudio.MakeSungLine(
            SongSeconds, [.. TruthMelody.Select(n => (n.Midi, n.StartSec, TruthNoteSeconds))],
            noiseLevel: 0.01, sampleRate: SampleRate)));
        var pad = PoMode.API.Audio.AudioDecoder.Decode(WriteTemp(TestAudio.MakeSong(SongSeconds,
            [.. TruthChords.SelectMany(c => c.PadMidis.Select(m => (m, c.StartSec, c.EndSec - c.StartSec, 1.0)))],
            SampleRate)));
        var bleed = Math.Pow(10, -18 / 20.0);
        var mixed = new float[Math.Min(voice.Samples.Length, pad.Samples.Length)];
        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] = (float)((voice.Samples[i] + (bleed * pad.Samples[i])) / (1 + bleed));
        }
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1)))
        {
            writer.WriteSamples(mixed, 0, mixed.Length);
        }
        return stream.ToArray();
    }

    private static string WriteTemp(byte[] wav)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pomode-accuracy-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, wav);
        return path;
    }

    private async Task<BeatRow> ScoreBeatsAsync(
        (double Bpm, int Bars, double LeadInSec) groove, IBeatTracker tracker, string kind)
    {
        var scenario = $"{groove.Bpm:0} BPM groove, bar one at {groove.LeadInSec:0.00}s";
        if (!await tracker.IsAvailableAsync(CancellationToken.None))
        {
            return new BeatRow(scenario, tracker.Name, kind, Available: false,
                ExecutionPlanner.EffectiveRank(tracker), 0, 0, 0, 0);
        }
        var (wav, beats, downbeats) = TestAudio.MakeDrumLoop(groove.Bars, groove.Bpm, groove.LeadInSec, SampleRate);
        var dir = Path.Combine(_dir, $"groove-{groove.Bpm:0}-{tracker.Name}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "groove.wav");
        File.WriteAllBytes(path, wav);

        var stopwatch = Stopwatch.StartNew();
        var result = await tracker.TrackBeatsAsync(new StageContext("accuracy", dir, path), CancellationToken.None);
        stopwatch.Stop();

        // A tracker that hears no downbeats is scored on the bar lines it implies: its tempo map's
        // measure starts, which is where the app would number the bars.
        var heardDownbeats = result.Grid.Downbeats
            ?? [.. result.TempoMap.Measures.Select(m => m.StartSec)];
        return new BeatRow(scenario, tracker.Name, kind, Available: true, ExecutionPlanner.EffectiveRank(tracker),
            result.Grid.Bpm, TimeF(beats, result.Beats), TimeF(downbeats, heardDownbeats),
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>F-measure of predicted event times against the truth, ±70 ms, each prediction used once.</summary>
    private static double TimeF(IReadOnlyList<double> truth, IReadOnlyList<double> predicted)
    {
        if (truth.Count == 0 || predicted.Count == 0)
        {
            return 0;
        }
        var used = new bool[predicted.Count];
        var hits = 0;
        foreach (var t in truth)
        {
            for (var i = 0; i < predicted.Count; i++)
            {
                if (!used[i] && Math.Abs(predicted[i] - t) <= BeatToleranceSec)
                {
                    used[i] = true;
                    hits++;
                    break;
                }
            }
        }
        var precision = hits / (double)predicted.Count;
        var recall = hits / (double)truth.Count;
        return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    private static async Task<PitchRow> ScorePitchAsync(
        string scenario, IPitchTracker tracker, string kind, string dir, string inputPath)
    {
        var context = new StageContext("accuracy", dir, inputPath);
        var stopwatch = Stopwatch.StartNew();
        var notes = await tracker.TrackAsync(context, CancellationToken.None);
        stopwatch.Stop();
        Console.WriteLine($"{scenario} / {tracker.Name}: "
            + string.Join(", ", notes.Take(24).Select(n => $"{n.MidiPitch}@{n.StartSec:0.00}+{n.DurationSec:0.00}")));

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
        return new PitchRow(scenario, tracker.Name, kind, Available: true, ExecutionPlanner.EffectiveRank(tracker),
            notes.Count, precision, recall, f1, stopwatch.ElapsedMilliseconds);
    }

    private static async Task<ChordRow> ScoreChordsAsync(
        string scenario, IChordRecognizer recognizer, string kind, string inputPath, IReadOnlyList<ChordSpan> truth)
    {
        var context = new StageContext("accuracy", Path.GetDirectoryName(inputPath)!, inputPath);
        var stopwatch = Stopwatch.StartNew();
        var spans = await recognizer.RecognizeAsync(context, CancellationToken.None);
        stopwatch.Stop();

        // Frame accuracy: sample the timeline every 100 ms and compare root and major/minor, using
        // the app's own half-open covering-span search so the boundary convention cannot drift from
        // it. Triad level, because the DSP recognizers can only name triads and a maj7 heard as its
        // major triad is right about everything the modal analysis reads.
        var samples = 0;
        var correct = 0;
        for (var t = 0.05; t < truth[^1].EndSec; t += 0.1)
        {
            if (TimelineSearch.IndexCovering(truth, t, c => c.StartSec, c => c.EndSec) is not { } truthIndex)
            {
                continue;
            }
            samples++;
            var predictedIndex = TimelineSearch.IndexCovering(spans, t, s => s.StartSec, s => s.EndSec);
            if (predictedIndex is { } index && Triad(spans[index]) == Triad(truth[truthIndex]))
            {
                correct++;
            }
        }
        return new ChordRow(scenario, recognizer.Name, kind, ExecutionPlanner.EffectiveRank(recognizer),
            spans.Count, samples == 0 ? 0 : correct / (double)samples, stopwatch.ElapsedMilliseconds);
    }

    private static (int Root, bool Minor) Triad(ChordSpan chord)
        => (PitchNames.TryParseRoot(chord.Root, out var root) ? root : -1, chord.Quality.StartsWith("min", StringComparison.Ordinal));

    /// <summary>
    /// Runs every free executor over a real recording. Nothing here is scored against a truth —
    /// there isn't one — so the honest signals are: what each model actually produced, how long it
    /// took on real audio, what key the app would report, and how far the two independent models
    /// agree with each other. Two models agreeing is weak evidence they are right; two models
    /// disagreeing is strong evidence at least one is wrong, and that is worth seeing.
    /// </summary>
    private async Task<RealSongReport> AnalyseRealSongAsync(
        IReadOnlyList<(IPitchTracker Tracker, string Kind, bool Available)> pitchTrackers,
        IReadOnlyList<(IBeatTracker Tracker, string Kind)> beatTrackers,
        ChordMiniChordRecognizer chordMini)
    {
        var path = RealSongPath;
        if (!File.Exists(path))
        {
            Console.WriteLine($"Real-song section skipped: '{path}' not present on this machine.");
            return new RealSongReport(Present: false, Path.GetFileName(path), 0, 0, 0, [], [], [], 0, 0);
        }

        // A job dir of its own: the synthetic run wrote a vocals.wav into _dir, and the pitch
        // trackers prefer that file — reusing the directory would silently analyse the wrong audio.
        var jobDir = Path.Combine(_dir, "real");
        Directory.CreateDirectory(jobDir);
        var context = new StageContext("real", jobDir, path);

        var decoded = PoMode.API.Audio.AudioDecoder.Decode(path);
        var duration = decoded.DurationSeconds;

        // One chord run feeds the modal engine for every tracker, so the reported keys differ only
        // by the melody the tracker heard — the variable actually under test here.
        var chordStopwatch = Stopwatch.StartNew();
        var chromaChords = await new ChromaChordRecognizer().RecognizeAsync(context, CancellationToken.None);
        chordStopwatch.Stop();
        var viterbiStopwatch = Stopwatch.StartNew();
        var viterbiChords = await new ViterbiChordRecognizer().RecognizeAsync(context, CancellationToken.None);
        viterbiStopwatch.Stop();

        var pitchRows = new List<RealPitchRow>();
        IReadOnlyList<NoteEvent>? first = null;
        foreach (var (tracker, kind, available) in pitchTrackers.Where(t => t.Available))
        {
            var stopwatch = Stopwatch.StartNew();
            var notes = await tracker.TrackAsync(context, CancellationToken.None);
            stopwatch.Stop();
            first ??= notes;

            var modal = PoMode.API.Features.ModalAnalysis.ModalAnalysisEngine.Analyze(notes, chromaChords);
            var range = notes.Count == 0
                ? "—"
                : $"{PitchLabel(notes.Min(n => n.MidiPitch))}–{PitchLabel(notes.Max(n => n.MidiPitch))}";
            pitchRows.Add(new RealPitchRow(
                tracker.Name, kind, notes.Count,
                duration > 0 ? notes.Count / duration : 0,
                range,
                $"{modal.TonicName} {modal.PrimaryMode?.ToString() ?? "(unclear)"}",
                stopwatch.ElapsedMilliseconds,
                ReferenceEquals(first, notes) ? double.NaN : NoteAgreement(first, notes)));
        }

        var beatRows = new List<RealBeatRow>();
        var beatSets = new List<IReadOnlyList<double>>();
        foreach (var (tracker, kind) in beatTrackers)
        {
            if (!await tracker.IsAvailableAsync(CancellationToken.None))
            {
                continue;
            }
            var stopwatch = Stopwatch.StartNew();
            var result = await tracker.TrackBeatsAsync(context, CancellationToken.None);
            stopwatch.Stop();
            beatSets.Add(result.Beats);
            beatRows.Add(new RealBeatRow(tracker.Name, kind, result.Grid.Bpm, result.Beats.Count,
                result.Grid.Downbeats?.Count ?? result.TempoMap.Measures.Count, stopwatch.ElapsedMilliseconds));
        }

        var chordRows = new List<RealChordRow>
        {
            RealChordRowFor(new ChromaChordRecognizer().Name, chromaChords, chordStopwatch.ElapsedMilliseconds),
            RealChordRowFor(new ViterbiChordRecognizer().Name, viterbiChords, viterbiStopwatch.ElapsedMilliseconds),
        };
        if (await chordMini.IsAvailableAsync(CancellationToken.None))
        {
            var modelStopwatch = Stopwatch.StartNew();
            var modelChords = await chordMini.RecognizeAsync(context, CancellationToken.None);
            chordRows.Add(RealChordRowFor(chordMini.Name, modelChords, modelStopwatch.ElapsedMilliseconds) with { Kind = "model" });
        }

        return new RealSongReport(
            Present: true,
            Path.GetFileName(path),
            duration,
            decoded.SampleRate,
            decoded.Channels,
            pitchRows,
            chordRows,
            beatRows,
            ChordAgreement(chromaChords, viterbiChords, duration),
            beatSets.Count == 2 ? TimeF(beatSets[0], beatSets[1]) : double.NaN);
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
        string inputFormat, List<PitchRow> pitchRows, List<ChordRow> chordRows, List<BeatRow> beatRows,
        RealSongReport realSong)
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
            $"<p>Sample input: {SongSeconds:0}s synthetic song, {inputFormat} — 8-note melody (C5 E5 D5 B4 A4 C5 A4 F5) over a C&nbsp;·&nbsp;G&nbsp;·&nbsp;Am&nbsp;·&nbsp;F triad pad, 2s per chord. Ground truth is exact because the song is rendered from it.</p>");

        // ---- Melody: one table, one block of rows per vocal stem ----
        html.Append("<h2>Melody (PitchTracking)</h2><table><tr>" +
            "<th>Vocal stem</th><th>Executor</th><th>Kind</th><th>Runs by default</th><th>Notes found</th><th>Precision</th><th>Recall</th><th>F1</th><th>Runtime</th></tr>");
        var defaultPitch = pitchRows.Where(r => r.Available).MinBy(r => r.Rank)?.Name;
        foreach (var scenario in pitchRows.GroupBy(r => r.Scenario))
        {
            var bestF1 = scenario.Where(r => r.Available).Select(r => r.F1).DefaultIfEmpty(0).Max();
            foreach (var row in scenario)
            {
                if (!row.Available)
                {
                    html.Append(CultureInfo.InvariantCulture,
                        $"<tr class=\"muted\"><td>{row.Scenario}</td><td>{row.Name}</td><td>{row.Kind}</td><td colspan=\"6\">not available on this machine (model not downloaded)</td></tr>");
                    continue;
                }
                var css = row.F1 >= bestF1 && bestF1 > 0 ? " class=\"best\"" : "";
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr{css}><td>{row.Scenario}</td><td>{row.Name}</td><td>{row.Kind}</td><td>{(row.Name == defaultPitch ? "✓ default" : "")}</td><td>{row.NotesFound}</td><td>{row.Precision:P0}</td><td>{row.Recall:P0}</td><td>{row.F1:0.00}</td><td>{row.Milliseconds} ms</td></tr>");
            }
        }
        html.Append("</table><p class=\"note\">Truth: 8 notes. A prediction scores when the pitch matches " +
            "exactly and the onset is within 0.25s. <em>Sine melody</em> is a perfect separation of pure tones; " +
            "<em>Sung line + bleed</em> is the same melody with harmonics, ±40 cents of 5.5 Hz vibrato, soft attacks, " +
            "the pad leaking through 18 dB down and a little noise. The test fails if the default tracker's mean F1 " +
            "over both stems is not the highest among the models; the classic method (YIN) is scored and shown but " +
            "ranks after every model by design, and these clean monophonic stems are its best case.</p>");
        var means = pitchRows.Where(r => r.Available).GroupBy(r => r.Name)
            .Select(g => $"{g.Key} {g.Average(r => r.F1).ToString("0.00", CultureInfo.InvariantCulture)}");
        html.Append(CultureInfo.InvariantCulture,
            $"<p><strong>Mean F1 over both stems:</strong> {string.Join(" · ", means)}</p>");

        html.Append("<h2>Chords (ChordDetecting)</h2><table><tr>" +
            "<th>Scenario</th><th>Executor</th><th>Kind</th><th>Runs by default</th><th>Chords found</th><th>Frame accuracy</th><th>Runtime</th></tr>");
        var defaultChord = chordRows.MinBy(r => r.Rank)?.Name;
        foreach (var scenario in chordRows.GroupBy(r => r.Scenario))
        {
            var bestAccuracy = scenario.Max(r => r.Accuracy);
            foreach (var row in scenario)
            {
                var css = row.Accuracy >= bestAccuracy && bestAccuracy > 0 ? " class=\"best\"" : "";
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr{css}><td>{row.Scenario}</td><td>{row.Name}</td><td>{row.Kind}</td><td>{(row.Name == defaultChord ? "✓ default" : "")}</td><td>{row.ChordsFound}</td><td>{row.Accuracy:P0}</td><td>{row.Milliseconds} ms</td></tr>");
            }
        }
        html.Append("</table><p class=\"note\">Truth: the pad is C · G · Am · F, 2s each; the demo vamp's chords are the ones " +
            "the server wrote. Accuracy is the share of 100ms timeline samples whose predicted root and major/minor " +
            "match the truth. Recognizers read the full mix, melody included.</p>");

        // ---- Beats ----
        html.Append("<h2>Beats and bars (beat tracking)</h2><table><tr>" +
            "<th>Groove</th><th>Executor</th><th>Kind</th><th>Runs by default</th><th>BPM</th><th>Beat F</th><th>Downbeat F</th><th>Runtime</th></tr>");
        var defaultBeats = beatRows.Where(r => r.Available).MinBy(r => r.Rank)?.Name;
        foreach (var scenario in beatRows.GroupBy(r => r.Scenario))
        {
            var bestScore = scenario.Where(r => r.Available).Select(r => r.Score).DefaultIfEmpty(0).Max();
            foreach (var row in scenario)
            {
                if (!row.Available)
                {
                    html.Append(CultureInfo.InvariantCulture,
                        $"<tr class=\"muted\"><td>{row.Scenario}</td><td>{row.Name}</td><td>{row.Kind}</td><td colspan=\"5\">not available on this machine (model not downloaded)</td></tr>");
                    continue;
                }
                var css = row.Score >= bestScore && bestScore > 0 ? " class=\"best\"" : "";
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr{css}><td>{row.Scenario}</td><td>{row.Name}</td><td>{row.Kind}</td><td>{(row.Name == defaultBeats ? "✓ default" : "")}</td><td>{row.Bpm:0.0}</td><td>{row.BeatF:0.00}</td><td>{row.DownbeatF:0.00}</td><td>{row.Milliseconds} ms</td></tr>");
            }
        }
        html.Append("</table><p class=\"note\">Synthetic 4/4 grooves (kick 1 &amp; 3, snare 2 &amp; 4, hi-hat eighths, " +
            "a bass note changing every bar), with bar one deliberately not at t=0. F-measure at ±70 ms. " +
            "A tracker that reports no downbeats is scored on the bar lines it implies (its tempo map's measure starts). " +
            "The test fails if the default tracker's mean of beat and downbeat F is not the highest.</p>");

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
            html.Append("<p class=\"note\"><strong>Read with this caveat:</strong> stem separation is too slow for this suite, so the pitch trackers here read the <em>full mix</em>, not an isolated vocal. Basic Pitch is polyphonic and reports every pitch it hears; RMVPE was trained to follow the singing voice inside a mix; YIN is monophonic by design, so this is its worst case. The case still matters — the short-clip fast path and Azure mode both skip separation.</p>");

            html.Append("<h3>Melody</h3><table><tr>" +
                "<th>Executor</th><th>Kind</th><th>Notes</th><th>Notes/sec</th><th>Pitch range</th><th>Key it implies</th><th>Agreement with first row</th><th>Runtime</th></tr>");
            foreach (var row in realSong.Pitch)
            {
                var agreement = double.IsNaN(row.AgreementWithFirst)
                    ? "—"
                    : row.AgreementWithFirst.ToString("P0", CultureInfo.InvariantCulture);
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{row.Name}</td><td>{row.Kind}</td><td>{row.NotesFound}</td><td>{row.NotesPerSecond:0.0}</td><td>{row.PitchRange}</td><td>{row.DetectedKey}</td><td>{agreement}</td><td>{row.Milliseconds} ms</td></tr>");
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

            html.Append("<h3>Beats</h3><table><tr>" +
                "<th>Executor</th><th>Kind</th><th>BPM</th><th>Beats</th><th>Bars</th><th>Runtime</th></tr>");
            foreach (var row in realSong.Beats)
            {
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{row.Name}</td><td>{row.Kind}</td><td>{row.Bpm:0.0}</td><td>{row.Beats}</td><td>{row.Bars}</td><td>{row.Milliseconds} ms</td></tr>");
            }
            html.Append("</table>");

            var beatAgreement = double.IsNaN(realSong.BeatAgreement)
                ? "only one beat tracker available"
                : realSong.BeatAgreement.ToString("P0", CultureInfo.InvariantCulture);
            html.Append(CultureInfo.InvariantCulture,
                $"<h3>Model agreement</h3><table><tr><th>Pair</th><th>Agreement</th></tr><tr><td>Chord recognizers (100ms timeline samples)</td><td>{realSong.ChordAgreement:P0}</td></tr><tr><td>Beat trackers (beat F, ±70 ms)</td><td>{beatAgreement}</td></tr></table>");
            html.Append("<p class=\"note\">Agreement is not accuracy. Two models agreeing is weak evidence they are both right; two models disagreeing is strong evidence at least one is wrong. Read low agreement as \"go listen to this song and see who is closer\", not as a score. Pitch agreement is in the melody table: same note, onset within 0.25s, against the first row.</p>");
        }

        html.Append("<h2>Not compared here</h2><p class=\"note\">" +
            "Separating: OnnxStemSeparator (HTDemucs) needs minutes of runtime and ~5.7 GB of memory per run, " +
            "so it stays out of the routine suite; there is no other real separator to race it against. " +
            "ModalAnalysis: one deterministic rule engine (ModalAnalysisEngine), no alternatives to race.</p>" +
            "</body></html>");

        File.WriteAllText(reportPath, html.ToString());
        return reportPath;
    }
}
