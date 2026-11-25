// Port of immutable-dictionary.sb.
using System.Collections.Immutable;

[Benchmark("library/immutable-dictionary")]
static class LibraryImmutableDictionary {
    public static string Run() {
        var builder = ImmutableDictionary.CreateBuilder<long, long>();
        for (long i = 0; i < 200000; i++) {
            builder[checked(i * 7919)] = i;
        }
        var map = builder.ToImmutable();
        long total = 0;
        for (long i = 0; i < 400000; i++) {
            if (map.TryGetValue(checked(i * 7919), out var value)) {
                total = checked(total + value);
            }
        }
        var versions = new List<ImmutableDictionary<long, long>>();
        var current = map;
        for (long i = 0; i < 100000; i++) {
            current = current.SetItem(checked(i * 15838) % 1000003, i);
            if (i % 1000 == 0) {
                versions.Add(current);
            }
        }
        foreach (var version in versions) {
            total += version.Count;
        }
        builder = map.ToBuilder();
        for (long i = 0; i < 100000; i++) {
            builder.Remove(checked(i * 15838));
        }
        map = builder.ToImmutable();
        foreach (var (key, value) in map) {
            total += key ^ value;
        }
        return $"{total} {map.Count} {current.Count}";
    }
}
