// Port of immutable-dictionary.sb.
using System.Collections.Immutable;

[Benchmark("library/immutable-dictionary")]
static class LibraryImmutableDictionary {
    static long Key(long i) => checked((i * 1103515245 + 12345) % 2147483648);

    public static string Run() {
        checked {
            var builder = ImmutableDictionary.CreateBuilder<long, long>();
            for (long i = 0; i < 200000; i++) {
                builder[Key(i)] = i;
            }

            var map = builder.ToImmutable();
            long total = 0;
            for (long i = 0; i < 400000; i++) {
                if (map.TryGetValue(Key(i), out var value)) {
                    total += value;
                }
            }

            var versions = new List<ImmutableDictionary<long, long>>();
            var current = map;
            for (long i = 0; i < 100000; i++) {
                current = current.SetItem(Key(i * 3), i);
                if (i % 1000 == 0) {
                    versions.Add(current);
                }
            }

            foreach (var version in versions) {
                total += version.Count;
            }

            builder = map.ToBuilder();
            for (long i = 0; i < 100000; i++) {
                builder.Remove(Key(i * 2));
            }

            map = builder.ToImmutable();
            foreach (var (key, value) in map) {
                total = unchecked(total + (key ^ value));
            }

            return $"{total} {map.Count} {current.Count}";
        }
    }
}
