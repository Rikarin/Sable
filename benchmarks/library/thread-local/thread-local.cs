// Port of thread-local.sb: ThreadLocal<T>.
[Benchmark("library/thread-local")]
static class LibraryThreadLocal {
    public static string Run() {
        using var count = new ThreadLocal<long>(static () => 0);
        for (var i = 0; i < 10_000_000; i++) {
            count.Value = checked(count.Value + (i & 3));
        }

        return count.Value.ToString();
    }
}
