using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PoMode.API.Features.Analysis;

public sealed class JobQueue
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait });

    /// <summary>The ids in the channel, in order. The channel cannot be read without taking from it,
    /// and "how many are ahead of me" is a question about its contents.</summary>
    private readonly List<string> _waiting = [];

    public async ValueTask EnqueueAsync(string jobId, CancellationToken ct)
    {
        // Listed before it is written: an idle worker can take the id the instant it lands, and its
        // Remove must find it there or the id would sit in the list for good.
        lock (_waiting)
        {
            _waiting.Add(jobId);
        }
        try
        {
            await _channel.Writer.WriteAsync(jobId, ct);
        }
        catch
        {
            lock (_waiting)
            {
                _waiting.Remove(jobId);
            }
            throw;
        }
    }

    public async IAsyncEnumerable<string> DequeueAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var jobId in _channel.Reader.ReadAllAsync(ct))
        {
            lock (_waiting)
            {
                _waiting.Remove(jobId);
            }
            yield return jobId;
        }
    }

    /// <summary>The jobs still waiting, next to run first.</summary>
    public string[] Waiting()
    {
        lock (_waiting)
        {
            return [.. _waiting];
        }
    }

    /// <summary>Jobs that will run before this one, or null when it is not waiting. A job only waits
    /// while the single worker is busy, so the one running counts as well as those in front.</summary>
    public int? Ahead(string jobId) => Array.IndexOf(Waiting(), jobId) is >= 0 and var index ? index + 1 : null;

    /// <summary>Jobs waiting for the single-concurrency worker — surfaced by /diag.</summary>
    public int Depth => _channel.Reader.Count;
}
