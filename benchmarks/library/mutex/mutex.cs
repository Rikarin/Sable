// Four threads each taking the lock a quarter of a million times to add one
// to a shared count: .NET 11's Lock, through the 'lock' statement.
[Benchmark("library/mutex")]
static class LibraryMutex {
    public static string Run() {
        var gate = new Lock();
        long count = 0;
        var threads = new Thread[4];
        for (var t = 0; t < threads.Length; t++) {
            threads[t] = new Thread(() => {
                for (var i = 0; i < 250000; i++) {
                    lock (gate) {
                        count++;
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
