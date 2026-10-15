using PoMode.Shared.Analysis;
using PoMode.Shared.Hardware;

namespace PoMode.Shared.Diagnostics;

public sealed record DiagnosticsReport(
    string EnvironmentName,
    bool IsAzureHosted,
    string SecretSource,
    bool SecretFellBack,
    HardwareReport? Hardware,
    /// <summary>Jobs waiting for the single-concurrency worker right now.</summary>
    int QueueDepth = 0,
    /// <summary>What the planner would pick for an upload arriving now (no browser tier declared);
    /// null when no executor set is available at all.</summary>
    IReadOnlyList<StagePlan>? DefaultPlan = null,
    /// <summary>The guards and exporters that are switched on. Presence only, like every other field
    /// here — no endpoints, no keys, nothing that could carry a secret.</summary>
    OperationalReport? Operational = null,
    /// <summary>How each song interpreter has done since start-up (InterpreterStats).</summary>
    IReadOnlyList<InterpreterStatsDto>? Interpreters = null);

/// <summary>One interpreter's record since start-up: replies that passed the grounding check, replies
/// sent back for inventing a figure, and failures the next interpreter answered in its place.</summary>
public sealed record InterpreterStatsDto(string Name, int Replies, int GroundingRetries, int FellThrough);

/// <summary>
/// Which operational guards this instance is running with.
///
/// <para>Worth reporting because every one of them is invisible from the outside when it is working
/// and indistinguishable from a bug when it is not: a 429 from a rate limiter and a 429 from a
/// misconfigured proxy look identical to a user, and an app exporting no telemetry looks exactly
/// like an app whose collector is down.</para>
/// </summary>
public sealed record OperationalReport(
    bool RateLimitsEnabled,
    int MaxQueueDepth,
    /// <summary>Whether this instance has a VAPID key pair, i.e. can notify a closed tab at all.</summary>
    bool PushAvailable = false,
    /// <summary>Whether traces and metrics are exported to Application Insights. Presence only.</summary>
    bool TelemetryEnabled = false);
