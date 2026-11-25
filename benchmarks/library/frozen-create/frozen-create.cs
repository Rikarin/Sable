// Port of frozen-create.sb.
using System.Collections.Frozen;

[Benchmark("library/frozen-create")]
static class LibraryFrozenCreate {
    public static string Run() {
        long total = 0;
        for (var round = 0; round < 40; round++) {
            var numbers = Enumerable.Range(0, 2000).ToFrozenDictionary(i => i * 104729L + round, i => (long)i);
            var names = Enumerable.Range(0, 1000).ToFrozenDictionary(i => $"identifier{i + round}", i => (long)i);
            total += numbers[104729L * 1000 + round] + names[$"identifier{500 + round}"];
        }

        return $"{total}";
    }
}
