// Port of task-spawn.sb, with Task.Run.
[Benchmark("library/task-spawn")]
static class LibraryTaskSpawn {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        long total = 0;
        for (long batch = 0; batch < 100; batch++) {
            var tasks = new List<Task<long>>();
            for (long i = 0; i < 2000; i++) {
                var value = checked(i + batch);
                tasks.Add(Task.Run(() => value));
            }

            foreach (var task in tasks) {
                total = checked(total + await task);
            }
        }

        return total.ToString();
    }
}
