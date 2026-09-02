using Microsoft.Extensions.Time.Testing;
using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.PitchTracking;

public class ClientWorkRegistryTests
{
    private static readonly IReadOnlyList<NoteEvent> Notes = [new NoteEvent(60, 0.0, 0.5, 90)];

    [Fact]
    public async Task Completion_and_duplicate_prevention()
    {
        var registry = new ClientWorkRegistry(new FakeTimeProvider());
        var wait = registry.WaitAsync("job1", TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.True(registry.TryComplete("job1", Notes));
        Assert.False(registry.TryComplete("job1", [new NoteEvent(72, 1.0, 0.5, 90)]));
        Assert.Equal(Notes, await wait);
    }

    [Fact]
    public async Task Timeout_and_cancellation_leave_no_orphaned_waiters()
    {
        var time = new FakeTimeProvider();
        var registry = new ClientWorkRegistry(time);
        var wait = registry.WaitAsync("job1", TimeSpan.FromSeconds(300), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(301));
        await Assert.ThrowsAsync<TimeoutException>(() => wait);
        Assert.False(registry.IsWaiting("job1"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelledWait = registry.WaitAsync("job2", TimeSpan.FromMinutes(5), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        Assert.False(registry.IsWaiting("job2"));
    }
}
