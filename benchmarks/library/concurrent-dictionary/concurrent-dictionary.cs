// Four threads each counting a quarter of a million keys of 4096 with
// AddOrUpdate and reading one back with GetOrAdd.
[Benchmark("library/concurrent-dictionary")]
static class LibraryConcurrentDictionary {
    public static string Run() {
        checked {
            var counts = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();
            var threads = new Thread[4];
            for (var t = 0; t < threads.Length; t++) {
                var seed = t;
                threads[t] = new Thread(() => {
                    long x = seed * 7919 + 1;
                    for (var i = 0; i < 250000; i++) {
                        x = (x * 1103515245 + 12345) % 2147483648;
                        var key = x % 4096;
                        counts.AddOrUpdate(key, 1, static (_, v) => v + 1);
                        counts.GetOrAdd(key + 4096, static _ => 1);
                    }
                });
                threads[t].Start();
            }

            foreach (var thread in threads) {
                thread.Join();
            }

            long total = 0;
            foreach (var entry in counts) {
                total += entry.Value;
            }

            return $"{counts.Count} {total}";
        }
    }
}
