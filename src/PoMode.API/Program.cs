using Microsoft.AspNetCore.Authentication;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.Copilot;
using PoMode.API.Features.Hardware;
using PoMode.API.Features.MidiExport;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.Session;
using PoMode.API.Features.StemSeparation;
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
builder.Services.AddHealthChecks().AddCheck<JobStorageHealthCheck>("job-storage");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddSingleton<IStemSeparator, OnnxStemSeparator>();
builder.Services.AddSingleton<IStemSeparator, FakeStemSeparator>();
builder.Services.AddSingleton<IPitchTracker, OnnxPitchTracker>();
builder.Services.AddSingleton<IPitchTracker, FakePitchTracker>();
builder.Services.AddSingleton<IChordRecognizer, ChromaChordRecognizer>();
builder.Services.AddSingleton<IChordRecognizer, FakeChordRecognizer>();
builder.Services.AddSingleton<IModalAnalyzer, ArtifactModalAnalyzer>();
builder.Services.AddSingleton<ExecutionPlanner>();
builder.Services.AddSingleton<OllamaCopilotClient>();
builder.Services.AddSingleton<IAnalysisNotifier, SignalRAnalysisNotifier>();
builder.Services.AddSingleton<AnalysisPipeline>();
builder.Services.AddHostedService<AnalysisWorker>();
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
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapScalarApiReference(); // serves /scalar
app.MapHealthChecks("/health");
app.MapDiagnostics();

app.MapSession();
app.MapAnalysis();
app.MapMidiExport();
app.MapCopilot();
app.MapHub<AnalysisHub>("/hubs/analysis");

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
