using System.Collections.Concurrent;
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
    // Per-descriptor-key in-process lock. Tasks 5/8 call EnsureAsync from concurrent pipeline stages
    // for the same model; without this, two callers can both pass the File.Exists check and both try
    // to open the same .part file with FileShare.None, and the loser throws an unhandled IOException.
    // Mirrors JobStore's per-jobId SemaphoreSlim pattern. Not re-entrant — never acquire the same
    // descriptor's gate twice on one call stack.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public string RootPath { get; } =
        configuration["Models:RootPath"] ?? Path.Combine(AppContext.BaseDirectory, "models");

    public bool IsDownloaded(ModelDescriptor descriptor) =>
        File.Exists(Path.Combine(RootPath, descriptor.FileName));

    private SemaphoreSlim LockFor(string key) => _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Ensures <paramref name="descriptor"/>'s model file exists locally, downloading and verifying it
    /// if necessary, and returns its local path. No-ops when the file is already present. Throws
    /// immediately in Azure mode, before touching the network or the filesystem. Also throws
    /// immediately — before any network call — when <see cref="ModelDescriptor.Sha256"/> is empty: an
    /// unverified model must never be downloaded and trusted as-is, so every catalog entry must carry a
    /// real hash before this method will fetch it.
    ///
    /// Downloads to a <c>.part</c> file first; the downloaded bytes are hashed with <see cref="SHA256"/>
    /// and compared case-insensitively against <see cref="ModelDescriptor.Sha256"/> — on mismatch, or on
    /// any other failure while downloading (dropped connection, non-success status, cancellation), the
    /// <c>.part</c> file is deleted before the exception propagates, so a failed attempt never leaves
    /// partial bytes behind for a later call to mistake for a real download in progress.
    ///
    /// Concurrent callers for the same <see cref="ModelDescriptor.Key"/> are serialized on a per-key
    /// gate: only the first caller downloads; the rest wait for it and then no-op once the file exists.
    /// </summary>
    public async Task<string> EnsureAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        if (EnvironmentDetector.IsAzureHosted())
        {
            throw new InvalidOperationException("Local models are disabled in Azure mode.");
        }

        if (string.IsNullOrEmpty(descriptor.Sha256))
        {
            throw new InvalidOperationException(
                $"Model '{descriptor.Key}' has no SHA-256 hash configured; refusing to download an unverified model.");
        }

        Directory.CreateDirectory(RootPath);

        var finalPath = Path.Combine(RootPath, descriptor.FileName);
        var gate = LockFor(descriptor.Key);
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(finalPath))
            {
                return finalPath;
            }

            var partPath = finalPath + ".part";
            try
            {
                using var client = httpClientFactory.CreateClient(nameof(ModelRegistry));
                using (var response = await client.GetAsync(descriptor.Url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(ct);
                    await using var destination = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await source.CopyToAsync(destination, ct);
                }

                var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(partPath, ct)))
                    .ToLowerInvariant();
                if (!string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError(
                        "Model {Key} failed SHA-256 verification: expected {Expected}, got {Actual}.",
                        descriptor.Key, descriptor.Sha256, actualHash);
                    throw new InvalidOperationException(
                        $"Downloaded model '{descriptor.Key}' failed SHA-256 verification.");
                }
            }
            catch
            {
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                }
                throw;
            }

            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }
        finally
        {
            gate.Release();
        }
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
