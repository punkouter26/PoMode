using PoMode.Shared.Hardware;

namespace PoMode.Shared.Diagnostics;

public sealed record DiagnosticsReport(
    string EnvironmentName,
    bool IsAzureHosted,
    string SecretSource,
    bool SecretFellBack,
    IReadOnlyList<ProviderKeyStatus> ProviderKeys,
    /// <summary>Whether the paid cloud tier may be used at all (Cloud:Enabled). A configured key with
    /// this false means "credential present, spending forbidden" — worth seeing in diagnostics.</summary>
    bool CloudEnabled,
    HardwareReport? Hardware);

public sealed record ProviderKeyStatus(string Provider, bool Configured);
