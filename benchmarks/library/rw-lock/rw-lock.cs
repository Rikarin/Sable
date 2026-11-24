// Port of rw-lock.sb: four threads and ReaderWriterLockSlim.
[Benchmark("library/rw-lock")]
static class LibraryRwLock {
    public static string Run() {
        checked {
            using var gate = new ReaderWriterLockSlim();
            long count = 0;
            var threads = new Thread[4];
            for (var t = 0; t < threads.Length; t++) {
                var task = t;
                threads[t] = new Thread(() => {
                    long sum = 0;
                    for (var i = 0; i < 250000; i++) {
                        if (i % 100 == task) {
                            gate.EnterWriteLock();
                            try {
                                count++;
                            } finally {
                                gate.ExitWriteLock();
                            }
                        } else {
                            gate.EnterReadLock();
                            try {
                                sum += count % 2;
                            } finally {
                                gate.ExitReadLock();
                            }
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
