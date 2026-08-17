namespace PoMode.API.Infrastructure;

/// <summary>
/// Describes an ONNX model that <see cref="ModelRegistry"/> can download and verify on first use.
/// <paramref name="Sha256"/> must be a real, known-correct hash — <see cref="ModelRegistry.EnsureAsync"/>
/// treats an empty hash as a hard error rather than downloading an unverified model.
/// </summary>
public sealed record ModelDescriptor(string Key, string FileName, string Url, string Sha256);
