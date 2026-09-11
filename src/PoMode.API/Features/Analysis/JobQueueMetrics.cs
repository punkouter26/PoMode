using PoMode.API.Platform;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// Publishes the analysis queue depth as an observable gauge.
///
/// <para>A hosted service only because an observable gauge has to be created once and then left
/// alone — the callback is pulled by the metrics collector, so nothing here runs on a timer or holds
/// a thread. It lives beside <see cref="JobQueue"/> rather than inside <c>PoTelemetry</c> so the
/// platform file stays free of this app's own types, and it uses that file's meter rather than its
/// own so the gauge arrives on the same instrument name the exporter is already subscribed to.</para>
///
/// <para>Depth is the number that turns a slow page into a diagnosis: the worker runs one job at a
/// time, so a queue that never drains is the difference between "the model is slow" and "more work is
/// arriving than this box can do".</para>
/// </summary>
public sealed class JobQueueMetrics : IHostedService
{
    public JobQueueMetrics(JobQueue queue)
        => PoTelemetry.Meter.CreateObservableGauge(
            "pomode.jobs.queue_depth",
            () => queue.Depth,
            unit: "{job}",
            description: "Jobs waiting for the single-concurrency analysis worker.");

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
