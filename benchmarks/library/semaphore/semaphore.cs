// Port of semaphore.sb, with SemaphoreSlim.
[Benchmark("library/semaphore")]
static class LibrarySemaphore {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        using var semaphore = new SemaphoreSlim(2);
        long inside = 0;
        var tasks = new List<Task<long>>();
        for (var t = 0; t < 8; t++) {
            tasks.Add(Task.Run(async () => {
                long most = 0;
                for (var i = 0; i < 10000; i++) {
                    await semaphore.WaitAsync();
                    var now = Interlocked.Increment(ref inside);
                    most = now > most ? now : most;
                    Interlocked.Decrement(ref inside);
                    semaphore.Release();
                }

                return most <= 2 ? 10000L : 0L;
            }));
        }

        return (await Task.WhenAll(tasks)).Sum().ToString();
    }
}
