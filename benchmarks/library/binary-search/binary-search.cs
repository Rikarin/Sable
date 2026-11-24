// Port of binary-search.sb.
[Benchmark("library/binary-search")]
static class LibraryBinarySearch {
    public static string Run() {
        checked {
            var values = new List<long>();
            for (long i = 0; i < 1000000; i++) {
                values.Add(i * 3);
            }

            long x = 12345;
            long found = 0;
            long total = 0;
            for (var i = 0; i < 2000000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                var index = values.BinarySearch(x % 3000000);
                if (index >= 0) {
                    found++;
                    total += index;
                }
            }

            return $"{found} {total}";
        }
    }
}
