using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PoMode.Shared.Hardware;

namespace PoMode.API.Infrastructure;

/// <summary>
/// Downloads and SHA-256-verifies ONNX models on first use, caching them under <see cref="RootPath"/>.
/// Models are never committed to source control (<c>models/</c> is git-ignored) and are never
/// downloaded in Azure mode — local inference is a desktop/on-prem feature only.
/// </summary>
public sealed class ModelRegistry(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<ModelRegistry> logger)
{
    public string RootPath { get; } =
        configuration["Models:RootPath"] ?? Path.Combine(AppContext.BaseDirectory, "models");

    public bool IsDownloaded(ModelDescriptor descriptor) =>
        File.Exists(Path.Combine(RootPath, descriptor.FileName));

    /// <summary>
    /// Ensures <paramref name="descriptor"/>'s model file exists locally, downloading and verifying it
    /// if necessary, and returns its local path. No-ops when the file is already present. Throws
    /// immediately in Azure mode, before touching the network. Downloads to a <c>.part</c> file first;
    /// when <see cref="ModelDescriptor.Sha256"/> is non-empty, the downloaded bytes are hashed with
    /// <see cref="SHA256"/> and compared case-insensitively — on mismatch the <c>.part</c> file is
    /// deleted and an <see cref="InvalidOperationException"/> is thrown. When
    /// <see cref="ModelDescriptor.Sha256"/> is empty, verification is skipped (no verified hash is
    /// known yet for that model) and the download is trusted as-is.
    /// </summary>
    public async Task<string> EnsureAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        if (EnvironmentDetector.IsAzureHosted())
        {
            throw new InvalidOperationException("Local models are disabled in Azure mode.");
        }

        Directory.CreateDirectory(RootPath);

        var finalPath = Path.Combine(RootPath, descriptor.FileName);
        if (File.Exists(finalPath))
        {
            return finalPath;
        }

        var partPath = finalPath + ".part";
        var client = httpClientFactory.CreateClient(nameof(ModelRegistry));

        using (var response = await client.GetAsync(descriptor.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, ct);
        }

        if (!string.IsNullOrEmpty(descriptor.Sha256))
        {
            var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(partPath, ct)))
                .ToLowerInvariant();
            if (!string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partPath);
                logger.LogError(
                    "Model {Key} failed SHA-256 verification: expected {Expected}, got {Actual}.",
                    descriptor.Key, descriptor.Sha256, actualHash);
                throw new InvalidOperationException(
                    $"Downloaded model '{descriptor.Key}' failed SHA-256 verification.");
            }
        }
        else
        {
            logger.LogWarning(
                "Model {Key} has no known SHA-256 hash; skipping verification.", descriptor.Key);
        }

        File.Move(partPath, finalPath, overwrite: true);
        return finalPath;
    }

    public IReadOnlyList<ModelStatus> StatusFor(IEnumerable<ModelDescriptor> descriptors) =>
        descriptors
            .Select(descriptor =>
            {
                var path = Path.Combine(RootPath, descriptor.FileName);
                var available = File.Exists(path);
                var size = available ? new FileInfo(path).Length : 0L;
                return new ModelStatus(descriptor.Key, available, size);
            })
            .ToArray();
}
