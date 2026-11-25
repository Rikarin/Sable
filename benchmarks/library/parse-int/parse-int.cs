// Port of parse-int.sb.
[Benchmark("library/parse-int")]
static class LibraryParseInt {
    public static Func<string> Setup() {
        checked {
            var texts = new List<string>();
            long x = 12345;
            for (var i = 0; i < 200000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                texts.Add($"{(i % 2 == 0 ? x : -x)}");
            }
            return () => Run(texts);
        }
    }

    static string Run(List<string> texts) {
        checked {
            long total = 0;
            for (var round = 0; round < 5; round++) {
                foreach (var text in texts) {
                    total += long.Parse(text);
                }
            }

            return total.ToString();
        }
    }
}
