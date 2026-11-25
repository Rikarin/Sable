// Port of parallel-sum.sb: PLINQ over a range.
[Benchmark("library/parallel-sum")]
static class LibraryParallelSum {
    public static string Run() {
        var total = ParallelEnumerable.Range(0, 30_000_000).Sum(static n => unchecked((long)n * n) & 1023);
        return total.ToString();
    }
}
