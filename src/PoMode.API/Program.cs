using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Batch;
using PoMode.API.Features.ChordChart;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.Diagnostics;
using PoMode.API.Features.Library;
using PoMode.API.Features.Live;
using PoMode.API.Features.MidiExport;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.ModalMelodies;
using PoMode.API.Features.MusicXml;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.Session;
using PoMode.API.Features.SongStatistics;
using PoMode.API.Features.StemSeparation;
using PoMode.API.Features.UrlIngest;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Serialization;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = AudioFormatValidator.MaxBytes);

var secretSource = SecretsBootstrap.Configure(builder);
builder.Services.AddSingleton(secretSource);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, PoModeJsonContext.Default));

if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "FakeAuthHandler must never run in Production. Configure a real authentication provider.");
}

builder.Services.AddAuthentication(FakeAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ModelRegistry>();
builder.Services.AddSingleton<HardwareProbe>();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddHealthChecks()
    .AddCheck<JobStorageHealthCheck>("job-storage");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobBlobStorage>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<BatchStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<AnalysisIntake>();
builder.Services.AddSingleton<UrlAudioService>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddSingleton<IStemSeparator, OnnxStemSeparator>();
builder.Services.AddSingleton<IStemSeparator, FakeStemSeparator>();
builder.Services.AddSingleton<OnnxPitchTracker>();
builder.Services.AddSingleton<IPitchTracker>(sp => sp.GetRequiredService<OnnxPitchTracker>());
builder.Services.AddSingleton<IPitchTracker, ClientDelegatedPitchTracker>();
builder.Services.AddSingleton<IPitchTracker, YinPitchTracker>();
builder.Services.AddSingleton<IPitchTracker, FakePitchTracker>();
builder.Services.AddSingleton<IChordRecognizer, ChromaChordRecognizer>();
builder.Services.AddSingleton<IChordRecognizer, ViterbiChordRecognizer>();
builder.Services.AddSingleton<IChordRecognizer, FakeChordRecognizer>();
builder.Services.AddSingleton<ArtifactModalAnalyzer>();
builder.Services.AddSingleton<ModalMelodyGenerator>();
builder.Services.AddSingleton<ISongInterpreter, OllamaSongInterpreter>();
builder.Services.AddSingleton<ISongInterpreter, TemplateSongInterpreter>();
builder.Services.AddSingleton<SongInterpreterSelector>();
builder.Services.AddSingleton<ClientWorkRegistry>();
builder.Services.AddSingleton<ExecutionPlanner>();
builder.Services.AddSingleton<IAnalysisNotifier, SignalRAnalysisNotifier>();
builder.Services.AddSingleton<AnalysisPipeline>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<JobRecoveryService>();
builder.Services.AddHostedService<JobCleanupService>();
builder.Services.AddHostedService<ModelWarmupService>();
builder.Services.AddSignalR();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(
    options => options.MultipartBodyLengthLimit = AudioFormatValidator.MaxBytes);

var app = builder.Build();

if (secretSource.FellBack)
{
    app.Logger.LogWarning("Key Vault unreachable — secrets are coming from environment variables this run.");
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = served =>
    {
        // The app's own scripts and styles carry no fingerprint in their URL, so a browser left to its
        // own heuristics will keep serving a copy from before the last deploy. That is how a page ends
        // up calling into a JS module that no longer matches the C# beside it, and the symptom is an
        // exported function the page swears does not exist. "no-cache" still lets the browser store
        // the file; it just has to revalidate first, so an unchanged one costs a 304 and nothing more.
        // The fingerprinted framework files come through UseBlazorFrameworkFiles and are not touched.
        var name = served.File.Name;
        if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            served.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapScalarApiReference(); // serves /scalar
app.MapHealthChecks("/health");
// Liveness runs no checks (is the process serving requests at all); readiness runs them all.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapDiagnostics();

app.MapSession();
app.MapAnalysis();
app.MapBatch();
app.MapLibrary();
app.MapLive();
app.MapUrlIngest();
app.MapWebRuntime();
app.MapModalMelodies();
app.MapMidiExport();
app.MapMusicXmlExport();
app.MapChordChart();
app.MapSongStats();
app.MapHub<AnalysisHub>("/hubs/analysis");

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
