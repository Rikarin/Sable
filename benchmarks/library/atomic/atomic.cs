// Port of atomic.sb: four threads.
[Benchmark("library/atomic")]
static class LibraryAtomic {
    public static string Run() {
        checked {
            long count = 0;
            var threads = new Thread[4];
            for (var t = 0; t < threads.Length; t++) {
                threads[t] = new Thread(() => {
                    for (var i = 0; i < 500000; i++) {
                        Interlocked.Increment(ref count);
                    }

                    for (var i = 0; i < 500000; i++) {
                        var seen = Volatile.Read(ref count);
                        while (true) {
                            var was = Interlocked.CompareExchange(ref count, seen + 1, seen);
                            if (was == seen) {
                                break;
                            }

                            seen = was;
                        }
                    }
                });
                threads[t].Start();
            }

            foreach (var thread in threads) {
                thread.Join();
            }

            return count.ToString();
        }
    }
}
