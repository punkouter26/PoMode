using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;

namespace PoMode.API.Features.Analysis;

public enum BlobMirrorStatus { Disabled, Connected, Unreachable }

/// <summary>
/// Durable blob mirror for the job folder — Azurite in Development, Azure Storage (Managed
/// Identity via <c>Jobs:Storage:AccountUri</c>) in Production. The local job folder stays the
/// fast working copy; every state/artifact write is mirrored here, and reads fall back to the
/// mirror when the local copy is gone (restart, new host). <c>Jobs:Storage:Mode</c> is
/// <c>Auto</c> (default: probe once at first use, run filesystem-only when unreachable) or
/// <c>FileSystem</c> (mirror off). The probe result is cached for the process lifetime, so
/// starting Azurite after the API requires an API restart to pick it up.
/// </summary>
public sealed class JobBlobStorage(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<JobBlobStorage> logger)
{
    public const string AzuriteConnectionString = "UseDevelopmentStorage=true";

    private readonly object _gate = new();
    private Task<BlobContainerClient?>? _container;

    private string Mode => configuration["Jobs:Storage:Mode"] ?? "Auto";
    private string ContainerName => configuration["Jobs:Storage:Container"] is { Length: > 0 } name ? name : "pomode-jobs";
    private bool FileSystemOnly => string.Equals(Mode, "FileSystem", StringComparison.OrdinalIgnoreCase);

    public async Task MirrorFileAsync(string jobId, string fileName, string localPath, CancellationToken ct)
    {
        var container = await GetContainerAsync();
        if (container is null)
        {
            return;
        }
        try
        {
            await using var stream = File.OpenRead(localPath);
            await container.GetBlobClient($"{jobId}/{fileName}").UploadAsync(stream, overwrite: true, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The local folder is still authoritative — a mirror outage must never fail a job.
            logger.LogWarning(ex, "Could not mirror {File} for job {JobId} to blob storage.", fileName, jobId);
        }
    }

    public async Task MirrorDirectoryAsync(string jobId, string directory, CancellationToken ct)
    {
        var container = await GetContainerAsync();
        if (container is null)
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (Path.GetExtension(file) == ".tmp")
            {
                continue;
            }
            await MirrorFileAsync(jobId, Path.GetFileName(file), file, ct);
        }
    }

    public async Task<byte[]?> TryDownloadAsync(string jobId, string fileName, CancellationToken ct)
    {
        var container = await GetContainerAsync();
        if (container is null)
        {
            return null;
        }
        try
        {
            var response = await container.GetBlobClient($"{jobId}/{fileName}").DownloadContentAsync(ct);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not download {File} for job {JobId} from blob storage.", fileName, jobId);
            return null;
        }
    }

    /// <summary>Deletes one blob. Synchronous because the purge sweeps are.</summary>
    public void DeleteBlob(string jobId, string fileName)
    {
        var task = GetContainerAsync();
        if (!task.IsCompletedSuccessfully || task.Result is not { } container)
        {
            return;
        }
        try
        {
            container.DeleteBlobIfExists($"{jobId}/{fileName}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete blob {File} for {JobId}.", fileName, jobId);
        }
    }

    /// <summary>Deletes every blob under the job's prefix. Synchronous because the purge sweep is.</summary>
    public void DeleteJobBlobs(string jobId)
    {
        var task = GetContainerAsync();
        if (!task.IsCompletedSuccessfully || task.Result is not { } container)
        {
            return; // not connected (or still probing) — the next sweep retries
        }
        try
        {
            foreach (var blob in container.GetBlobs(prefix: $"{jobId}/"))
            {
                container.DeleteBlobIfExists(blob.Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not purge blobs for job {JobId}.", jobId);
        }
    }

    public async Task<BlobMirrorStatus> ProbeAsync(CancellationToken ct)
    {
        if (FileSystemOnly)
        {
            return BlobMirrorStatus.Disabled;
        }
        var container = await GetContainerAsync();
        if (container is null)
        {
            return BlobMirrorStatus.Unreachable;
        }
        try
        {
            await container.GetPropertiesAsync(conditions: null, cancellationToken: ct);
            return BlobMirrorStatus.Connected;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Blob mirror probe failed.");
            return BlobMirrorStatus.Unreachable;
        }
    }

    private Task<BlobContainerClient?> GetContainerAsync()
    {
        lock (_gate)
        {
            return _container ??= ConnectAsync();
        }
    }

    private async Task<BlobContainerClient?> ConnectAsync()
    {
        if (FileSystemOnly)
        {
            return null;
        }
        var accountUri = configuration["Jobs:Storage:AccountUri"];
        if (string.IsNullOrEmpty(accountUri) && environment.IsProduction())
        {
            // Never silently degrade in Production: storage must come from Managed Identity there.
            throw new InvalidOperationException(
                "Jobs:Storage:AccountUri must be configured in Production. The Azurite fallback is for local development only.");
        }
        try
        {
            var service = string.IsNullOrEmpty(accountUri)
                ? new BlobServiceClient(AzuriteConnectionString)
                : new BlobServiceClient(new Uri(accountUri), new DefaultAzureCredential());
            var container = service.GetBlobContainerClient(ContainerName);
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await container.CreateIfNotExistsAsync(cancellationToken: probeTimeout.Token);
            logger.LogInformation("Job blob mirror connected to container '{Container}'.", ContainerName);
            return container;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Blob storage unreachable — running filesystem-only. Start Azurite with 'docker compose up -d' and restart the API to enable the mirror.");
            return null;
        }
    }
}
