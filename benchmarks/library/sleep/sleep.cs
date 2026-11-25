// Port of sleep.sb, with Task.Delay.
[Benchmark("library/sleep")]
static class LibrarySleep {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var tasks = new List<Task<long>>();
        for (long i = 0; i < 20000; i++) {
            var value = i;
            tasks.Add(Task.Run(async () => {
                await Task.Delay(TimeSpan.FromMilliseconds(value % 20 + 1));
                await Task.Delay(TimeSpan.FromMilliseconds(value % 7 + 1));
                return value;
            }));
        }

        long total = 0;
        foreach (var task in tasks) {
            total = checked(total + await task);
        }

        return total.ToString();
    }
}
