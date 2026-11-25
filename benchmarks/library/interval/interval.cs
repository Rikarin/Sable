// Port of interval.sb, with PeriodicTimer.
[Benchmark("library/interval")]
static class LibraryInterval {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var tasks = new List<Task<long>>();
        for (var i = 0; i < 1000; i++) {
            tasks.Add(Task.Run(async () => {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
                long ticks = 0;
                while (ticks < 50 && await timer.WaitForNextTickAsync()) {
                    ticks++;
                }

                return ticks;
            }));
        }

        return (await Task.WhenAll(tasks)).Sum().ToString();
    }
}
