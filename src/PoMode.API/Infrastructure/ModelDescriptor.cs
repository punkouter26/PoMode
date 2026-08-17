namespace PoMode.API.Infrastructure;

/// <summary>
/// Describes an ONNX model that <see cref="ModelRegistry"/> can download and verify on first use.
/// <paramref name="Sha256"/> may be empty when no verified hash is known yet; in that case
/// <see cref="ModelRegistry.EnsureAsync"/> skips hash verification (see remarks there).
/// </summary>
public sealed record ModelDescriptor(string Key, string FileName, string Url, string Sha256);
