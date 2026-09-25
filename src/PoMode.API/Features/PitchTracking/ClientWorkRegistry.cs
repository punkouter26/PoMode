using System.Collections.Concurrent;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// The rendezvous between a parked stage and the browser doing its work (spec §4's ClientDelegated
/// protocol): pitch tracking waits for notes, separation for a vocal stem. The pipeline thread awaits
/// <see cref="WaitAsync"/>; the matching endpoint calls <see cref="TryComplete"/> when the result
/// arrives, or <see cref="TryDecline"/> when the browser says it will not do the work.
///
/// <para>Every exit path removes the waiter — completion, decline, timeout, and cancellation alike. A
/// leaked waiter would make a later job with the same id resolve instantly with a stale result, and
/// would leak memory for every browser that walked away.</para>
/// </summary>
public sealed class ClientWorkRegistry<TResult>(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TResult>> _waiters = new();

    public bool IsWaiting(string jobId) => _waiters.ContainsKey(jobId);

    /// <summary>
    /// Parks until the browser posts a result for this job, or the timeout expires. Throws
    /// <see cref="TimeoutException"/> on expiry so the pipeline's existing tier fallback takes over —
    /// a silent browser must never wedge a job.
    /// </summary>
    public async Task<TResult> WaitAsync(string jobId, TimeSpan timeout, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // A job re-enqueued after a restart parks again; the stale waiter must not swallow the answer.
        _waiters.AddOrUpdate(jobId, completion, (_, existing) =>
        {
            existing.TrySetCanceled();
            return completion;
        });

        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        var expiry = Task.Delay(timeout, time, CancellationToken.None);
        try
        {
            var finished = await Task.WhenAny(completion.Task, expiry);
            if (finished == expiry && !completion.Task.IsCompleted)
            {
                throw new TimeoutException(
                    $"The browser did not return its work for job {jobId} within {timeout.TotalSeconds:0} s.");
            }
            return await completion.Task;
        }
        finally
        {
            // Only remove our own entry: a newer waiter may already have replaced it.
            if (_waiters.TryGetValue(jobId, out var current) && ReferenceEquals(current, completion))
            {
                _waiters.TryRemove(jobId, out _);
            }
        }
    }

    /// <summary>
    /// Hands the browser's result to the parked stage. False when nothing is waiting — an unknown job,
    /// a job that already timed out, or a duplicated POST — so the caller can answer 404 rather than
    /// pretending to accept work nobody wants.
    /// </summary>
    public bool TryComplete(string jobId, TResult result)
        => _waiters.TryRemove(jobId, out var completion) && completion.TrySetResult(result);

    /// <summary>
    /// The browser will not do this work (no fast enough runtime, a failed model download), so the
    /// stage falls through to the next tier now rather than after the whole timeout.
    /// </summary>
    public bool TryDecline(string jobId, string reason)
        => _waiters.TryRemove(jobId, out var completion)
            && completion.TrySetException(new InvalidOperationException($"The browser declined: {reason}"));
}
