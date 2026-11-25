// Port of parallel-sort.sb: PLINQ's OrderBy.
[Benchmark("library/parallel-sort")]
static class LibraryParallelSort {
    public static Func<string> Setup() {
        var numbers = new long[1_000_000];
        for (var i = 0; i < numbers.Length; i++) {
            numbers[i] = unchecked(i * 2_654_435_761L) & 0xF_FFFF;
        }

        return () => Run(numbers);
    }

    static string Run(long[] numbers) {
        var sorted = numbers.AsParallel().OrderBy(static n => n).ToArray();
        return $"{sorted[0]} {sorted[500_000]} {sorted[999_999]}";
    }
}
