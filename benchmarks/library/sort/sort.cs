// Port of sort.sb.
[Benchmark("library/sort")]
static class LibrarySort {
    public static string Run() {
        checked {
            var numbers = new List<long>();
            long x = 42;
            for (var i = 0; i < 1000000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                numbers.Add(x);
            }

            var words = new List<string>();
            for (var i = 0; i < 200000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                words.Add($"w{x % 1000000}");
            }

            // Sable orders strings by Unicode scalar, as ordinal comparison does for these.
            var a = new List<long>(numbers);
            a.Sort();
            var b = new List<string>(words);
            b.Sort(string.CompareOrdinal);
            var ordered = true;
            for (var i = 1; i < a.Count; i++) {
                if (a[i] < a[i - 1]) { ordered = false; }
            }

            for (var i = 1; i < b.Count; i++) {
                if (string.CompareOrdinal(b[i], b[i - 1]) < 0) { ordered = false; }
            }

            return $"{(ordered ? "true" : "false")} {a[0]} {a[a.Count / 2]} {b[0]} {b[^1]}";
        }
    }
}
