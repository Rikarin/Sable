// Port of parse-float.sb.
[Benchmark("library/parse-float")]
static class LibraryParseFloat {
    public static string Run() {
        checked {
            var texts = new List<string>();
            long x = 12345;
            for (var i = 0; i < 200000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                var whole = x;
                x = (x * 1103515245 + 12345) % 2147483648;
                texts.Add($"{whole % 100000}.{x}");
            }

            var total = 0.0;
            for (var round = 0; round < 5; round++) {
                foreach (var text in texts) {
                    total += double.Parse(text);
                }
            }

            return total.ToString("F3");
        }
    }
}
