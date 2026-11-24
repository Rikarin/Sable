// 200,000 pseudo-random keys inserted into a SortedDictionary, each looked
// up, a third removed, then the rest gone through in order and the keys of
// a range counted (GetViewBetween's count, as SortedSet has no ranged view
// on SortedDictionary: the keys are walked in order as Sable's range does).
[Benchmark("library/sorted-dictionary")]
static class LibrarySortedDictionary {
    public static string Run() {
        checked {
            const int n = 200000;
            var keys = new long[n];
            long x = 12345;
            for (var i = 0; i < n; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                keys[i] = x;
            }

            var d = new SortedDictionary<long, long>();
            for (var i = 0; i < n; i++) {
                d[keys[i]] = i;
            }

            long found = 0;
            for (var i = 0; i < n; i++) {
                if (d.TryGetValue(keys[i], out var value)) {
                    found += value % 7;
                }
            }

            for (var i = 0; i < n; i++) {
                if (i % 3 == 0) {
                    d.Remove(keys[i]);
                }
            }

            long total = 0;
            long previous = -1;
            var ordered = true;
            foreach (var entry in d) {
                if (entry.Key <= previous) {
                    ordered = false;
                }

                previous = entry.Key;
                total = (total + entry.Value) % 1000000007;
            }

            long inRange = 0;
            foreach (var entry in d) {
                if (entry.Key >= 500000000 && entry.Key < 1000000000) {
                    inRange++;
                }
            }

            return $"{d.Count} {found} {total} {inRange} {(ordered ? "true" : "false")}";
        }
    }
}
