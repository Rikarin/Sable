// Port of frozen-string.sb.
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

[Benchmark("library/frozen-string")]
static class LibraryFrozenString {
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Look(FrozenDictionary<string, long> frozen, List<string> keys, int rounds) {
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

    static IEnumerable<KeyValuePair<string, long>> Entries(int count, Func<int, string> text) =>
        Enumerable.Range(0, count).Select(i => new KeyValuePair<string, long>(text(i), i));

    static List<string> Keys(int count, Func<int, string> text) => Enumerable.Range(0, count).Select(text).ToList();

    public static string Run() {
        var small = Entries(30, i => $"k{i * 77}").ToFrozenDictionary();
        var names = Entries(200, i => $"identifier{i}").ToFrozenDictionary();
        var paths = Entries(5000, i => $"src/module/{i * 7}/file.sb").ToFrozenDictionary();
        var headers = Entries(50, i => $"X-Header-{i * 31}-Name").ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        var total = Look(small, Keys(45, i => $"k{i * 77}"), 40000);
        total += Look(names, Keys(300, i => $"identifier{i}"), 6000);
        total += Look(paths, Keys(7500, i => $"src/module/{i * 7}/file.sb"), 150);
        total += Look(headers, Keys(75, i => $"x-header-{i * 31}-NAME"), 20000);
        return $"{total}";
    }
}
