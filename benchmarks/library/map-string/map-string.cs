// Port of map-string.sb.
[Benchmark("library/map-string")]
static class LibraryMapString {
    public static string Run() {
        checked {
            const int n = 200000;
            var keys = new List<string>();
            for (long i = 0; i < n; i++) {
                keys.Add($"key-{i * 7919 % 1000003}");
            }

            var map = new Dictionary<string, long>();
            for (var i = 0; i < n; i++) {
                map[keys[i]] = i;
            }

            long sum = 0;
            for (var round = 0; round < 5; round++) {
                foreach (var key in keys) {
                    sum += map.TryGetValue(key, out var value) ? value : 0;
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

            return $"{map.Count} {sum} {hits}";
        }
    }
}
