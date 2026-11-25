// Port of frozen-set.sb.
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

[Benchmark("library/frozen-set")]
static class LibraryFrozenSet {
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Within<E>(FrozenSet<E> frozen, List<E> values, int rounds) {
        checked {
            long total = 0;
            for (var r = 0; r < rounds; r++) {
                foreach (var value in values) {
                    if (frozen.Contains(value)) {
                        total++;
                    }
                }
            }

            return total;
        }
    }

    static List<T> Each<T>(int count, Func<int, T> make) => Enumerable.Range(0, count).Select(make).ToList();

    public static string Run() {
        var words = Each(300, i => $"word-{i * 31}").ToFrozenSet();
        var numbers = Each(300, i => i * 104729L).ToFrozenSet();
        var texts = Each(1000, i => $"word-{i * 10}");
        var candidates = Each(1000, i => i * 35000L);
        var someNumbers = Each(300, i => i * 104729L * 3);
        return $"{Within(words, texts, 3000)} {Within(numbers, candidates, 4000)} {Within(numbers, someNumbers, 10000)}";
    }
}
