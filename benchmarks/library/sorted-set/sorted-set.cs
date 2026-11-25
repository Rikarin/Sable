// Port of sorted-set.sb.
[Benchmark("library/sorted-set")]
static class LibrarySortedSet {
    public static Func<string> Setup() {
        checked {
            var values = new List<long>();
            long x = 12345;
            for (var i = 0; i < 200000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                values.Add(x);
            }
            return () => Run(values);
        }
    }

    static string Run(List<long> values) {
        checked {
            var members = new SortedSet<long>();
            foreach (var value in values) {
                members.Add(value);
            }

            var hits = 0;
            foreach (var value in values) {
                if (members.Contains(value)) { hits++; }
                if (members.Contains(value + 2147483648)) { hits++; }
            }

            for (var i = 0; i < 200000; i += 3) {
                members.Remove(values[i]);
            }

            long total = 0;
            foreach (var value in members) {
                total = (total * 31 + value) % 1000000007;
            }

            return $"{members.Count} {hits} {total} {members.Min} {members.Max}";
        }
    }
}
