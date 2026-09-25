using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Demo;

/// <summary>
/// Gives each user one copy of the finished demo, once.
///
/// <para>Seeding happens where "your library is empty" is actually observed — the library read — rather
/// than at guest creation or behind an endpoint the client must remember to call. Guest creation would
/// miss Microsoft sign-ins and anyone who arrived while the template was still being built, and never
/// try again; an explicit endpoint would put the "is it empty" decision in the client and cost every
/// page a second round trip. The ledger makes the read idempotent: the first empty read after the
/// template is ready seeds, and no read after that ever does, so deleting the demo (or the 7-day sweep
/// taking it) does not bring it back.</para>
/// </summary>
public sealed class DemoLibrary(
    JobStore store,
    JobBlobStorage blobs,
    IConfiguration configuration,
    ILogger<DemoLibrary> logger)
{
    /// <summary>Blob prefix for the ledger's mirror. Not a job id, so no job sweep ever touches it.</summary>
    private const string LedgerBlobPrefix = "_demo";

    private const string LedgerFileName = "demo-seeded.txt";

    /// <summary>One gate for every seed: the ledger is one append-only file, and a user's two tabs
    /// racing through an empty library must produce one copy, not two. Held only on the empty-library
    /// path, so an established library never waits on it.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HashSet<string>? _seeded;

    /// <summary>Off in the test fixtures, like <c>RateLimits:Enabled</c>: a template build would take
    /// the single worker away from the jobs under test, and a demo row would sit in libraries they
    /// assert on.</summary>
    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue("Demo:Enabled", true);

    /// <summary>A file, not a folder, in the jobs root: every sweep and listing walks directories only.</summary>
    private string LedgerPath => Path.Combine(store.RootPath, LedgerFileName);

    /// <summary>
    /// The user's demo copy, if this is the moment to hand one out: demo on, never seeded before,
    /// template finished. Null otherwise — including when the template is still building, which is
    /// what makes a first boot harmless: the library is simply demo-less until a later read.
    /// </summary>
    public async Task<JobState?> TrySeedAsync(string ownerId, CancellationToken ct)
    {
        if (!IsEnabled(configuration))
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var seeded = _seeded ??= await LoadLedgerAsync(ct);
            // Input first: a template whose folder is gone must not be half-restored from blob by
            // the load below and then copied without its artifacts.
            if (seeded.Contains(ownerId)
                || store.TryFindInputPath(DemoSong.TemplateJobId) is null
                || await store.LoadAsync(DemoSong.TemplateJobId, ct) is not { Stage: JobStage.Complete } template)
            {
                return null;
            }

            var copy = await store.CopyAsync(template, ownerId, ct);
            seeded.Add(ownerId);
            await File.AppendAllTextAsync(LedgerPath, ownerId + "\n", ct);
            await blobs.MirrorFileAsync(LedgerBlobPrefix, LedgerFileName, LedgerPath, ct);
            logger.LogInformation("Seeded demo job {JobId} into a new library.", copy.JobId);
            return copy;
        }
        catch (IOException ex)
        {
            // The template being rebuilt underneath the copy, or a full disk. The library still
            // answers; the next read tries again because the ledger was not written.
            logger.LogWarning(ex, "Could not copy the demo into a new library.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HashSet<string>> LoadLedgerAsync(CancellationToken ct)
    {
        if (!File.Exists(LedgerPath)
            && await blobs.TryDownloadAsync(LedgerBlobPrefix, LedgerFileName, ct) is { } mirrored)
        {
            Directory.CreateDirectory(store.RootPath);
            await File.WriteAllBytesAsync(LedgerPath, mirrored, ct);
        }
        return File.Exists(LedgerPath)
            ? [.. (await File.ReadAllLinesAsync(LedgerPath, ct)).Where(line => line.Length > 0)]
            : [];
    }
}
