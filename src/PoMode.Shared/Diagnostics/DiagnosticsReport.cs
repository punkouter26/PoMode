using PoMode.Shared.Hardware;

namespace PoMode.Shared.Diagnostics;

public sealed record DiagnosticsReport(
    string EnvironmentName,
    bool IsAzureHosted,
    string SecretSource,
    bool SecretFellBack,
    IReadOnlyList<ProviderKeyStatus> ProviderKeys,
    HardwareReport? Hardware);

public sealed record ProviderKeyStatus(string Provider, bool Configured);
