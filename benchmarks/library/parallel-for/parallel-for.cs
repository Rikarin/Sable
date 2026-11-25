using System.Collections.Concurrent;

// Port of parallel-for.sb: Parallel.For, and Parallel.ForEach over a range's
// partitions.
[Benchmark("library/parallel-for")]
static class LibraryParallelFor {
    public static string Run() {
        var cells = new long[1000];
        for (var round = 0; round < 1000; round++) {
            var add = round;
            Parallel.For(0, cells.Length, i => cells[i] += add);
        }

        long total = 0;
        Parallel.ForEach(Partitioner.Create(0, 10_000_000), chunk => {
            long sum = 0;
            for (var i = chunk.Item1; i < chunk.Item2; i++) {
                sum += i & 7;
            }

            Interlocked.Add(ref total, sum);
        });

        long calls = 0;
        Parallel.For(0, 1_000_000, i => {
            if (i % 1000 == 0) {
                Interlocked.Increment(ref calls);
            }
        });

        long all = 0;
        foreach (var cell in cells) {
            all += cell;
        }

        return $"{all} {total} {calls}";
    }
}
