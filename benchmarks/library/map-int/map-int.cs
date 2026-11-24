// Port of map-int.sb.
[Benchmark("library/map-int")]
static class LibraryMapInt {
    public static string Run() {
        checked {
            const int n = 1000000;
            var keys = new List<long>();
            long x = 12345;
            for (var i = 0; i < n; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                keys.Add(x);
            }

            var map = new Dictionary<long, long>();
            for (var i = 0; i < n; i++) {
                map[keys[i]] = i;
            }

            long sum = 0;
            for (var round = 0; round < 3; round++) {
                foreach (var key in keys) {
                    sum += map.TryGetValue(key, out var value) ? value : 0;
                }
            }

            long misses = 0;
            for (var i = 0; i < n; i++) {
                if (!map.ContainsKey(keys[i] + 2147483648)) {
                    misses++;
                }
            }

            for (var i = 0; i < n; i += 2) {
                map.Remove(keys[i]);
            }

            long hits = 0;
            foreach (var key in keys) {
                if (map.ContainsKey(key)) {
                    hits++;
                }
            }

            return $"{map.Count} {sum} {misses} {hits}";
        }
    }
}
