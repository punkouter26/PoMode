using System.Collections.Concurrent;
using PoMode.Shared.Diagnostics;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// How each interpreter has done since this instance started: replies that passed, replies sent back
/// because they invented a figure, and times it failed outright and the next one wrote instead. A
/// model whose retry rate climbs is drifting from the prompt; one that keeps falling through is
/// broken while the template quietly answers in its place, which nothing else would show.
/// </summary>
public sealed class InterpreterStats
{
    private sealed class Counters
    {
        public int Replies;
        public int Retries;
        public int FellThrough;
    }

    private readonly ConcurrentDictionary<string, Counters> _byName = new();

    public void Replied(string name) => Interlocked.Increment(ref For(name).Replies);

    public void Retried(string name) => Interlocked.Increment(ref For(name).Retries);

    public void FellThrough(string name) => Interlocked.Increment(ref For(name).FellThrough);

    public IReadOnlyList<InterpreterStatsDto> Snapshot()
        => [.. _byName.OrderBy(pair => pair.Key).Select(pair =>
            new InterpreterStatsDto(pair.Key, pair.Value.Replies, pair.Value.Retries, pair.Value.FellThrough))];

    private Counters For(string name) => _byName.GetOrAdd(name, _ => new Counters());
}
