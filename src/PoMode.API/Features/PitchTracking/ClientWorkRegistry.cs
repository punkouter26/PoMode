using System.Collections.Concurrent;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// The rendezvous between a parked pitch-tracking stage and the browser doing the work (spec §4's
/// ClientDelegated protocol). The pipeline thread awaits <see cref="WaitAsync"/>; the
/// <c>client-result</c> endpoint calls <see cref="TryComplete"/> when the notes arrive.
///
/// <para>Every exit path removes the waiter — completion, timeout, and cancellation alike. A leaked
/// waiter would make a later job with the same id resolve instantly with stale notes, and would leak
/// memory for every browser that walked away.</para>
/// </summary>
public sealed class ClientWorkRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<NoteEvent>>> _waiters = new();

    public bool IsWaiting(string jobId) => _waiters.ContainsKey(jobId);

    /// <summary>
    /// Parks until the browser posts notes for this job, or the timeout expires. Throws
    /// <see cref="TimeoutException"/> on expiry so the pipeline's existing tier fallback takes over —
    /// a silent browser must never wedge a job.
    /// </summary>
    public async Task<IReadOnlyList<NoteEvent>> WaitAsync(
        string jobId, TimeSpan timeout, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<NoteEvent>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

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
                    $"The browser did not return pitch-tracking results for job {jobId} within {timeout.TotalSeconds:0} s.");
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
    /// Hands the browser's notes to the parked stage. False when nothing is waiting — an unknown job,
    /// a job that already timed out, or a duplicated POST — so the caller can answer 404 rather than
    /// pretending to accept work nobody wants.
    /// </summary>
    public bool TryComplete(string jobId, IReadOnlyList<NoteEvent> notes)
        => _waiters.TryRemove(jobId, out var completion) && completion.TrySetResult(notes);
}
