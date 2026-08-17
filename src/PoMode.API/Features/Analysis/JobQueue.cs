using System.Threading.Channels;

namespace PoMode.API.Features.Analysis;

public sealed class JobQueue
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait });

    public ValueTask EnqueueAsync(string jobId, CancellationToken ct) => _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Jobs waiting for the single-concurrency worker — surfaced by /diag.</summary>
    public int Depth => _channel.Reader.Count;
}
