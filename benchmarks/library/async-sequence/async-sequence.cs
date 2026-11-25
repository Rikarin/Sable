// Port of async-sequence.sb, with System.Linq.AsyncEnumerable.
[Benchmark("library/async-sequence")]
static class LibraryAsyncSequence {
    public static string Run() => RunAsync().GetAwaiter().GetResult();

    static async Task<string> RunAsync() {
        var squares = await Enumerable.Range(0, 1000000).ToAsyncEnumerable()
            .Where(static x => x % 3 != 0)
            .Select(static x => checked(x * 2L + 1))
            .Take(600000)
            .AggregateAsync(0L, static (total, x) => checked(total + x));
        long chunks = 0;
        await foreach (var chunk in Enumerable.Range(0, 500000).ToAsyncEnumerable().Chunk(16)) {
            chunks = checked(chunks + (long)chunk.Length * chunk[0]);
        }

        long zipped = 0;
        await foreach (var (a, b) in Enumerable.Range(0, 500000).ToAsyncEnumerable().Zip(Enumerable.Range(0, 500000).ToAsyncEnumerable().Select(static x => checked(x + 1)))) {
            zipped = checked(zipped + (a ^ b));
        }

        return $"{squares} {chunks} {zipped}";
    }
}
