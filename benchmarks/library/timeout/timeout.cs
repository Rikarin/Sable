// Port of timeout.sb: the .NET way is a CancellationTokenSource that cancels
// itself after the time, handed to the work.
[Benchmark("library/timeout")]
static class LibraryTimeout {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<long> Work(long value, CancellationToken token) {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        return value;
    }

    static async Task<string> RunAsync() {
        var tasks = new List<Task<long>>();
        for (var t = 0; t < 100; t++) {
            tasks.Add(Task.Run(async () => {
                long sum = 0;
                for (long i = 0; i < 400; i++) {
                    using var source = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    sum = checked(sum + await Work(i, source.Token));
                }

                return sum;
            }));
        }

        return (await Task.WhenAll(tasks)).Sum().ToString();
    }
}
