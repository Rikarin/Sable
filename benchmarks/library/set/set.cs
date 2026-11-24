// Port of set.sb.
[Benchmark("library/set")]
static class LibrarySet {
    public static string Run() {
        checked {
            var a = new HashSet<long>();
            var b = new HashSet<long>();
            long x = 12345;
            for (var i = 0; i < 300000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                a.Add(x / 7 % 500000);
                x = (x * 1103515245 + 12345) % 2147483648;
                b.Add(x / 7 % 500000);
            }

            var both = new HashSet<long>(a);
            both.UnionWith(b);
            var common = new HashSet<long>(a);
            common.IntersectWith(b);
            var only = new HashSet<long>(a);
            only.ExceptWith(b);
            var hits = 0;
            for (long i = 0; i < 500000; i++) {
                if (both.Contains(i)) { hits++; }
                if (common.Contains(i)) { hits++; }
                if (only.Contains(i)) { hits++; }
            }

            return $"{a.Count} {b.Count} {both.Count} {common.Count} {only.Count} {hits}";
        }
    }
}
