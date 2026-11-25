// Port of task-local.sb: AsyncLocal<T>, set and put back.
[Benchmark("library/task-local")]
static class LibraryTaskLocal {
    static readonly AsyncLocal<int?> Level = new();

    static int Value => Level.Value ?? 1;

    public static string Run() {
        long total = 0;
        for (var i = 0; i < 1_000_000; i++) {
            total += Value;
        }

        Level.Value = 2;
        for (var i = 0; i < 4_000_000; i++) {
            total += Value;
        }

        Level.Value = null;
        for (var n = 0; n < 100_000; n++) {
            Level.Value = n & 3;
            total += Value;
            Level.Value = null;
        }

        return total.ToString();
    }
}
