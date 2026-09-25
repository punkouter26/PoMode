using PoMode.API.Platform;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Auth;
using PoMode.API.Features.BeatTracking;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.Demo;
using PoMode.API.Features.Diagnostics;
using PoMode.API.Features.Export;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.ModalMelodies;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.Push;
using PoMode.API.Features.SongStatistics;
using PoMode.API.Features.Separation;
using PoMode.API.Features.Uploads;
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

// Guest and Microsoft sign-in in every environment; FakeAuth headers in Development and Test only.
builder.AddPoAuthentication();
// App Service terminates TLS in front of Kestrel. Without the forwarded scheme the OIDC redirect URI
// is built as http://, which does not match the app registration and the sign-in fails at Microsoft.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
// Admission control for the two costly things here: queueing an analysis and running a language
// model. Deliberately not global — see PoRateLimits.
builder.Services.AddPoRateLimiting(builder.Configuration);
builder.Services.AddSingleton<ModelRegistry>();
builder.Services.AddSingleton<HardwareProbe>();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddHealthChecks()
    .AddCheck<JobStorageHealthCheck>("job-storage");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobBlobStorage>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<AnalysisIntake>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddSingleton<IStemSeparator, OnnxStemSeparator>();
builder.Services.AddSingleton<IStemSeparator, FakeStemSeparator>();
// RMVPE and Basic Pitch share a rank (both local models), so registration order decides the default:
// RMVPE first, on the accuracy numbers in CLAUDE.md. Basic Pitch still transcribes the backing stem.
builder.Services.AddSingleton<IPitchTracker, RmvpePitchTracker>();
builder.Services.AddSingleton<OnnxPitchTracker>();
builder.Services.AddSingleton<IPitchTracker>(sp => sp.GetRequiredService<OnnxPitchTracker>());
builder.Services.AddSingleton<IPitchTracker, ClientDelegatedPitchTracker>();
builder.Services.AddSingleton<IPitchTracker, YinPitchTracker>();
builder.Services.AddSingleton<IPitchTracker, FakePitchTracker>();
builder.Services.AddSingleton<IChordRecognizer, ChromaChordRecognizer>();
builder.Services.AddSingleton<IChordRecognizer, ViterbiChordRecognizer>();
builder.Services.AddSingleton<IChordRecognizer, FakeChordRecognizer>();
builder.Services.AddSingleton<IBeatTracker, BeatThisBeatTracker>();
builder.Services.AddSingleton<IBeatTracker, DspBeatTracker>();
builder.Services.AddSingleton<ArtifactModalAnalyzer>();
builder.Services.AddSingleton<ModalMelodyGenerator>();
builder.Services.AddSingleton<HumTakeSeeder>();
builder.Services.AddSingleton<HumTakeHistory>();
builder.Services.AddSingleton<ISongInterpreter, OllamaSongInterpreter>();
builder.Services.AddSingleton<ISongInterpreter, TemplateSongInterpreter>();
builder.Services.AddSingleton<SongInterpreterSelector>();
builder.Services.AddSingleton<ClientWorkRegistry>();
builder.Services.AddSingleton<ExecutionPlanner>();
builder.Services.AddSingleton<IAnalysisNotifier, SignalRAnalysisNotifier>();
// The notice for a closed tab: Web Push to the job's owner when it finishes. Off unless a VAPID pair
// is configured (Development generates a throwaway one) — see PushSettings.
builder.Services.AddSingleton(sp => PushSettings.Resolve(
    builder.Configuration, builder.Environment, sp.GetRequiredService<ILogger<PushSettings>>()));
builder.Services.AddSingleton<PushSubscriptionStore>();
builder.Services.AddSingleton<IJobOutcomeNotifier, WebPushOutcomeNotifier>();
// Bounded: a push service that hangs must not hold the worker slot for HttpClient's default 100s.
builder.Services.AddHttpClient(WebPushOutcomeNotifier.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<AnalysisPipeline>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHostedService<JobRecoveryService>();
builder.Services.AddHostedService<JobCleanupService>();
// The first-run demo: one real pipeline run into a template, then a file copy per new library.
builder.Services.AddSingleton<DemoLibrary>();
builder.Services.AddHostedService<DemoTemplateService>();
builder.Services.AddSingleton<ResumableUploads>();
builder.Services.AddHostedService<ResumableUploadCleanupService>();
builder.Services.AddHostedService<ModelWarmupService>();
builder.Services.AddSignalR();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(
    options => options.MultipartBodyLengthLimit = AudioFormatValidator.MaxBytes);

var app = builder.Build();

if (secretSource.FellBack)
{
    app.Logger.LogWarning("Key Vault unreachable — secrets are coming from environment variables this run.");
}
// Resolved now rather than on the first job to finish, so a missing or malformed key pair (or the
// Development fallback to a throwaway one) is in the startup log where someone will read it.
app.Services.GetRequiredService<PushSettings>();

app.UseForwardedHeaders();
app.UseBlazorFrameworkFiles();
// .webmanifest is not in every ASP.NET Core content-type table, and a manifest served as
// application/octet-stream is ignored by the browser without an error anyone would notice — the app
// simply stops being installable. Mapped explicitly rather than left to the default provider.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
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
// After authentication on purpose: the limiter partitions by signed-in user where there is one, and
// before that runs every authenticated caller would share their address's bucket.
app.UseRateLimiter();

app.MapOpenApi();
app.MapScalarApiReference(); // serves /scalar
app.MapHealthChecks("/health");
// Liveness runs no checks (is the process serving requests at all); readiness runs them all.
// Replaced this app's bespoke liveness payload with the shared one: three apps each
// answered /health/live in a different shape, so nothing could poll them uniformly.
app.MapPoLiveness();
app.MapHealthChecks("/health/ready");
app.MapDiagnostics();

app.MapAuth();
app.MapAnalysis();
app.MapResumableUploads();
app.MapLibrary();
app.MapWebRuntime();
app.MapModalMelodies();
app.MapMidiExport();
app.MapSongStats();
app.MapPush();
app.MapHub<AnalysisHub>("/hubs/analysis");

// The share target's fallback. Normally the service worker answers this POST and never lets it
// reach the server; this exists for the case where it is not controlling the page yet — a first
// visit, or a browser that dropped the registration. Redirecting is the honest response: the file is
// not recoverable here, but the user lands in the app rather than on a blank 200.
app.MapPost("/share-target", () => Results.Redirect("/"))
    .DisableAntiforgery()
    .ExcludeFromDescription();

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
