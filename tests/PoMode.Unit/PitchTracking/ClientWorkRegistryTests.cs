using Microsoft.Extensions.Time.Testing;
using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.PitchTracking;

/// <summary>
/// The registry is the whole of Tier 2's "park until the browser answers" behaviour, so its failure
/// modes matter more than its happy path: a browser that never replies, replies twice, or replies for
/// a job nobody is waiting on must never wedge a job or corrupt another one.
/// </summary>
public class ClientWorkRegistryTests
{
    private static readonly IReadOnlyList<NoteEvent> Notes = [new NoteEvent(60, 0.0, 0.5, 90)];

    private static ClientWorkRegistry Registry(TimeProvider? time = null)
        => new(time ?? new FakeTimeProvider());

    [Fact]
    public async Task A_waiter_is_completed_with_the_posted_notes()
    {
        var registry = Registry();
        var wait = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(registry.TryComplete("job1", Notes));

        Assert.Equal(Notes, await wait);
    }

    [Fact]
    public void A_job_nobody_is_waiting_for_cannot_be_completed()
    {
        var registry = Registry();

        Assert.False(registry.TryComplete("nobody", Notes));
        Assert.False(registry.IsWaiting("nobody"));
    }

    [Fact]
    public async Task A_second_completion_is_refused()
    {
        var registry = Registry();
        var wait = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(registry.TryComplete("job1", Notes));
        // The waiter is gone the moment it is satisfied, so a replayed or duplicated POST is refused
        // rather than overwriting an accepted result.
        Assert.False(registry.TryComplete("job1", [new NoteEvent(72, 1.0, 0.5, 90)]));

        Assert.Equal(Notes, await wait);
    }

    [Fact]
    public void A_registered_waiter_is_reported_as_waiting()
    {
        var registry = Registry();
        _ = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(registry.IsWaiting("job1"));
        Assert.False(registry.IsWaiting("job2"));
    }

    [Fact]
    public async Task A_silent_browser_times_out_and_leaves_no_waiter_behind()
    {
        var time = new FakeTimeProvider();
        var registry = Registry(time);
        var wait = registry.WaitAsync("job1", TimeSpan.FromSeconds(300), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(301));

        await Assert.ThrowsAsync<TimeoutException>(() => wait);
        // Critical: a leaked waiter would make a later job with the same id resolve instantly, and
        // would leak memory for every abandoned job.
        Assert.False(registry.IsWaiting("job1"));
    }

    [Fact]
    public async Task The_timeout_does_not_fire_early()
    {
        var time = new FakeTimeProvider();
        var registry = Registry(time);
        var wait = registry.WaitAsync("job1", TimeSpan.FromSeconds(300), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(299));
        await Task.Delay(20);

        Assert.False(wait.IsCompleted);
        Assert.True(registry.TryComplete("job1", Notes));
        Assert.Equal(Notes, await wait);
    }

    [Fact]
    public async Task Cancelling_the_job_removes_the_waiter()
    {
        var registry = Registry();
        using var cts = new CancellationTokenSource();
        var wait = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.False(registry.IsWaiting("job1"));
    }

    [Fact]
    public async Task Waiters_for_different_jobs_do_not_interfere()
    {
        var registry = Registry();
        var first = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), CancellationToken.None);
        var second = registry.WaitAsync("job2", TimeSpan.FromMinutes(5), CancellationToken.None);
        IReadOnlyList<NoteEvent> otherNotes = [new NoteEvent(64, 2.0, 1.0, 80)];

        Assert.True(registry.TryComplete("job2", otherNotes));

        Assert.False(first.IsCompleted);
        Assert.Equal(otherNotes, await second);
        Assert.True(registry.TryComplete("job1", Notes));
        Assert.Equal(Notes, await first);
    }

    [Fact]
    public async Task Re_registering_the_same_job_replaces_the_stale_waiter()
    {
        // A job re-enqueued after a restart parks again; the old waiter must not swallow the answer.
        var registry = Registry();
        var abandoned = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), CancellationToken.None);
        var fresh = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(registry.TryComplete("job1", Notes));

        Assert.Equal(Notes, await fresh);
        await Assert.ThrowsAnyAsync<Exception>(() => abandoned);
    }
}
