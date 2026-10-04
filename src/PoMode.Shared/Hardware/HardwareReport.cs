namespace PoMode.Shared.Hardware;

public sealed record ModelStatus(string Key, bool Available, long SizeBytes);

public sealed record HardwareReport(
    bool IsAzureHosted,
    IReadOnlyList<ModelStatus> Models);
