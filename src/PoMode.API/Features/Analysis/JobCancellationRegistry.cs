using System.Collections.Concurrent;

namespace PoMode.API.Features.Analysis;

public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public void Register(string jobId, CancellationTokenSource cts) => _running[jobId] = cts;

    public bool TryCancel(string jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public void Remove(string jobId) => _running.TryRemove(jobId, out _);
}
