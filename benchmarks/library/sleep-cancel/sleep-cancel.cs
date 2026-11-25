// Port of sleep-cancel.sb: a CancellationTokenSource for each task, as each
// is cancelled on its own, and no exception thrown for what is expected.
[Benchmark("library/sleep-cancel")]
static class LibrarySleepCancel {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var tasks = new List<(Task<long> Task, CancellationTokenSource Source)>();
        for (var i = 0; i < 50000; i++) {
            var source = new CancellationTokenSource();
            var token = source.Token;
            tasks.Add((Task.Run(async () => {
                await Task.Delay(TimeSpan.FromHours(1), token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                return token.IsCancellationRequested ? 1L : 0L;
            }), source));
        }

        foreach (var (_, source) in tasks) {
            source.Cancel();
        }

        long total = 0;
        foreach (var (task, source) in tasks) {
            total = checked(total + await task);
            source.Dispose();
        }

        return total.ToString();
    }
}
