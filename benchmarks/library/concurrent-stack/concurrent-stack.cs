// Port of concurrent-stack.sb.
[Benchmark("library/concurrent-stack")]
static class LibraryConcurrentStack {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var stack = new System.Collections.Concurrent.ConcurrentStack<long>();
        var tasks = new List<Task<long>>();
        for (var t = 0; t < 4; t++) {
            tasks.Add(Task.Run(() => {
                long sum = 0;
                for (long i = 0; i < 250000; i++) {
                    stack.Push(i);
                    if (i % 4 != 0 && stack.TryPop(out var value)) {
                        sum = checked(sum + value);
                    }
                }

                return sum;
            }));
        }

        var total = (await Task.WhenAll(tasks)).Sum();
        while (stack.TryPop(out var value)) {
            total = checked(total + value);
        }

        return total.ToString();
    }
}
