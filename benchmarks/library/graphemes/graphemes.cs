// Port of graphemes.sb, with StringInfo.
[Benchmark("library/graphemes")]
static class LibraryGraphemes {
    public static Func<string> Setup() {
        checked {
            var text = string.Concat(Enumerable.Repeat("Plain words, e\u0301te\u0301, мир, 世界 and \U0001F600\r\n", 20000));
            return () => Run(text);
        }
    }

    static string Run(string text) {
        checked {
            long total = 0;
            for (var round = 0; round < 3; round++) {
                total += new System.Globalization.StringInfo(text).LengthInTextElements;
            }

            return total.ToString();
        }
    }
}
