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
    IReadOnlyList<StagePlan>? DefaultPlan = null);
