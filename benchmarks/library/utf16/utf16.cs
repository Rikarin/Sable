// Port of utf16.sb.
[Benchmark("library/utf16")]
static class LibraryUtf16 {
    public static Func<string> Setup() {
        checked {
            var bytes = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Grüße, мир, 世界 😀 and plain words too. ", 20000)));
            return () => Run(bytes);
        }
    }

    static string Run(byte[] bytes) {
        checked {
            long total = 0;
            for (var round = 0; round < 50; round++) {
                var units = System.Text.Encoding.UTF8.GetString(bytes);
                var back = System.Text.Encoding.UTF8.GetBytes(units);
                total += units.Length + back.Length;
            }

            return total.ToString();
        }
    }
}
