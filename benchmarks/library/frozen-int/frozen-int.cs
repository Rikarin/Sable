// Port of frozen-int.sb.
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

[Benchmark("library/frozen-int")]
static class LibraryFrozenInt {
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Look(FrozenDictionary<long, long> frozen, List<long> keys, int rounds) {
        checked {
            long total = 0;
            for (var r = 0; r < rounds; r++) {
                foreach (var key in keys) {
                    total += frozen.TryGetValue(key, out var value) ? value : 1;
                }
            }

            return total;
        }
    }

    static long Run(int count, long spread) {
        checked {
            var pairs = new List<KeyValuePair<long, long>>();
            var keys = new List<long>();
            for (long i = 0; i < count; i++) {
                pairs.Add(new(i * spread * 2, i));
                keys.Add(i * spread * 2);
                keys.Add(i * spread * 2 + 1);
            }

            return Look(pairs.ToFrozenDictionary(), keys, 5000000 / count);
        }
    }

    public static string Run() => $"{Run(100, 1)} {Run(100, 104729)} {Run(10000, 1299709)}";
}
