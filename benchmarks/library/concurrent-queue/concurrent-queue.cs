// Two threads enqueue half a million numbers each while two dequeue them
// all and add them up.
[Benchmark("library/concurrent-queue")]
static class LibraryConcurrentQueue {
    public static string Run() {
        checked {
            var queue = new System.Collections.Concurrent.ConcurrentQueue<long>();
            long taken = 0;
            var sums = new long[2];
            var threads = new List<Thread>();
            for (var p = 0; p < 2; p++) {
                threads.Add(new Thread(() => {
                    for (long i = 0; i < 500000; i++) {
                        queue.Enqueue(i);
                    }
                }));
            }

            for (var c = 0; c < 2; c++) {
                var consumer = c;
                threads.Add(new Thread(() => {
                    long total = 0;
                    while (Interlocked.Read(ref taken) < 1000000) {
                        if (queue.TryDequeue(out var value)) {
                            total += value;
                            Interlocked.Increment(ref taken);
                        }
                    }

                    sums[consumer] = total;
                }));
            }

            foreach (var thread in threads) {
                thread.Start();
            }

            foreach (var thread in threads) {
                thread.Join();
            }

            return (sums[0] + sums[1]).ToString();
        }
    }
}
