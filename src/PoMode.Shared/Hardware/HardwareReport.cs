namespace PoMode.Shared.Hardware;

public sealed record GpuReport(
    string Vendor,
    long TotalVramMb,
    long FreeVramMb,
    bool CudaAvailable,
    bool DmlAvailable);

public sealed record ModelStatus(string Key, bool Available, long SizeBytes);

public sealed record HardwareReport(
    bool IsAzureHosted,
    GpuReport? Gpu,
    IReadOnlyList<string> ConfiguredProviders,
    IReadOnlyList<ModelStatus> Models);
