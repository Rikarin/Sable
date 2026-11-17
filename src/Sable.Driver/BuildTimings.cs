using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sable.Driver;

/// <summary>
///     Where a build's time went, phase by phase (<c>sable build --timings</c>).
///     Phases that run in parallel add up their own time, so the sum can be
///     more than the wall-clock time.
/// </summary>
public sealed class BuildTimings {
    readonly ConcurrentDictionary<string, long> _ticks = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<string> _order = new();
    readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>Times what follows until the result is disposed, under <paramref name="phase" />.</summary>
    public Scope Measure(string phase) => new(this, phase, Stopwatch.GetTimestamp());

    /// <summary>Like <see cref="Measure(string)" />, and nothing when there is nothing to record into.</summary>
    public static Scope Of(BuildTimings? timings, string phase) => new(timings!, phase, timings is null ? 0 : Stopwatch.GetTimestamp());

    /// <summary>Times <paramref name="action" /> under <paramref name="phase" />.</summary>
    public T Measure<T>(string phase, Func<T> action) {
        using var _ = Measure(phase);
        return action();
    }

    public void Add(string phase, TimeSpan elapsed) => Add(phase, elapsed.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond);

    /// <summary>Counts something a phase did (files parsed, objects reused, ...).</summary>
    public void Count(string what, int by = 1) {
        if (_counts.AddOrUpdate(what, by, (_, n) => n + by) == by) {
            _order.Enqueue("#" + what);
        }
    }

    void Add(string phase, long ticks) {
        if (_ticks.AddOrUpdate(phase, ticks, (_, t) => t + ticks) == ticks) {
            _order.Enqueue(phase);
        }
    }

    public TimeSpan this[string phase] => _ticks.TryGetValue(phase, out var t) ? Stopwatch.GetElapsedTime(0, t) : TimeSpan.Zero;

    public int CountOf(string what) => _counts.TryGetValue(what, out var n) ? n : 0;

    public IReadOnlyList<string> Phases => [.._order.Where(static p => !p.StartsWith('#')).Distinct()];

    /// <summary>The phases in the order they first ran, each with its time, then the counts.</summary>
    public void Write(TextWriter writer) {
        writer.WriteLine("     Timings");
        foreach (var entry in _order.Distinct()) {
            if (entry.StartsWith('#')) {
                writer.WriteLine($"{"",16}{entry[1..],-24}{_counts[entry[1..]],9}");
            } else {
                writer.WriteLine($"{"",16}{entry,-24}{this[entry].TotalMilliseconds,9:0.0} ms");
            }
        }
    }

    public readonly struct Scope(BuildTimings owner, string phase, long start) : IDisposable {
        public void Dispose() => owner?.Add(phase, Stopwatch.GetTimestamp() - start);
    }
}
