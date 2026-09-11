using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PoMode.API.Platform;

/// <summary>
/// This app's traces and metrics.
///
/// <para>The question these exist to answer is not "is the server up" — <c>/health</c> answers that —
/// but "which executor is actually doing the work, and how long is it taking". The pipeline chooses
/// between a local ONNX model, the browser, classic model-less DSP and a fake placeholder, falling
/// through on failure, and until now the only record of that choice was a per-job audit trail nobody
/// aggregates. A duration histogram tagged by stage, executor and tier turns it into the answer to the
/// question the tier system exists for: is the local model beating the DSP fallback, and by how much.
/// </para>
///
/// <para>Exporting is opt-in. With no <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> configured the instruments
/// are still created and still recorded to — they cost nothing without a listener, and
/// <c>dotnet-counters</c> can read them live — but nothing leaves the machine. A local-first music
/// tool must not start shipping telemetry somewhere because a package was added.</para>
/// </summary>
public static class PoTelemetry
{
    /// <summary>Also the OTLP service name and the activity source name; one spelling for both.</summary>
    public const string SourceName = "PoMode.Pipeline";

    /// <summary>Spans for the pipeline's own work — one per stage, plus one per executor attempt.</summary>
    public static ActivitySource Source { get; } = new(SourceName, PoPlatform.Version);

    /// <summary>
    /// This app's meter. Public so a feature can hang its own observable gauge off it — the
    /// alternative, a second <see cref="Meter"/> per feature, means a second name for the exporter to
    /// be told about and one that will be forgotten.
    /// </summary>
    public static Meter Meter { get; } = new(SourceName, PoPlatform.Version);

    /// <summary>Jobs accepted for analysis, whatever becomes of them.</summary>
    public static Counter<long> JobsStarted { get; } = Meter.CreateCounter<long>(
        "pomode.jobs.started", unit: "{job}", description: "Analysis jobs queued.");

    /// <summary>Jobs that reached a terminal state, tagged <c>outcome</c>.</summary>
    public static Counter<long> JobsFinished { get; } = Meter.CreateCounter<long>(
        "pomode.jobs.finished", unit: "{job}", description: "Analysis jobs that reached a terminal state.");

    /// <summary>
    /// Wall-clock time one stage took, tagged with the stage and the executor that actually ran it —
    /// which is not always the one that was planned. Seconds rather than milliseconds because stem
    /// separation is measured in minutes and a millisecond histogram's buckets would all be the top one.
    /// </summary>
    public static Histogram<double> StageDuration { get; } = Meter.CreateHistogram<double>(
        "pomode.stage.duration", unit: "s", description: "Time one pipeline stage took.");

    /// <summary>
    /// Times a stage fell through from its planned executor to another. The single most useful
    /// operational number this app produces: a local model quietly failing on every job looks exactly
    /// like a working app from the outside, because the DSP fallback answers and the page renders.
    /// </summary>
    public static Counter<long> StageFallbacks { get; } = Meter.CreateCounter<long>(
        "pomode.stage.fallbacks", unit: "{fallback}", description: "Stage fell through to a later executor.");

    /// <summary>
    /// Wires tracing and metrics. Safe to call unconditionally: without an OTLP endpoint this only
    /// creates the in-process meters and listeners.
    /// </summary>
    public static IServiceCollection AddPoTelemetry(
        this IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var exporting = !string.IsNullOrWhiteSpace(endpoint);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(PoPlatform.AppName, serviceVersion: PoPlatform.Version))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(SourceName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        // The canvas polls artifacts and SignalR holds a long-lived connection; both
                        // would bury the handful of spans anyone wants to look at.
                        options.Filter = context => !IsNoise(context.Request.Path);
                    })
                    .AddHttpClientInstrumentation();
                if (exporting)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(SourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (exporting)
                {
                    metrics.AddOtlpExporter();
                }
            });

        return services;
    }

    /// <summary>
    /// Paths not worth a span. Health probes and the WASM payload are polled or huge, the hub is one
    /// connection held open for the length of a session, and none of the three tells anyone anything
    /// about how the app is behaving.
    /// </summary>
    private static bool IsNoise(PathString path)
        => path.StartsWithSegments("/health")
            || path.StartsWithSegments("/hubs")
            || path.StartsWithSegments("/_framework")
            || path.StartsWithSegments("/_content");

    /// <summary>
    /// Opens a span for one pipeline stage. Null when nothing is listening, which is the normal case
    /// and costs nothing — callers must handle it, hence the <c>?.</c> at every use site.
    /// </summary>
    public static Activity? StartStage(string stage, string jobId)
        => Source.StartActivity($"stage {stage}", ActivityKind.Internal)
            ?.SetTag("pomode.stage", stage)
            .SetTag("pomode.job_id", jobId);
}
