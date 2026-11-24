// Port of ordered-dictionary.sb.
[Benchmark("library/ordered-dictionary")]
static class LibraryOrderedDictionary {
    public static string Run() {
        checked {
            var dictionary = new OrderedDictionary<long, long>();
            var keys = new List<long>();
            long x = 12345;
            for (var i = 0; i < 300000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                keys.Add(x);
                dictionary[x] = i;
            }

            long total = 0;
            for (var round = 0; round < 3; round++) {
                foreach (var key in keys) {
                    total += dictionary[key];
                }
            }

            for (var i = 0; i < dictionary.Count; i++) {
                var (key, value) = dictionary.GetAt(i);
                total = (total + key * 3 + value) % 1000000007;
            }

            return $"{dictionary.Count} {total}";
        }
    }
}
