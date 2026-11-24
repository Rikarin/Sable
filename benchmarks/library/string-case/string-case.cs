// Port of string-case.sb.
[Benchmark("library/string-case")]
static class LibraryStringCase {
    public static string Run() {
        checked {
            var ascii = new List<string>();
            var mixed = new List<string>();
            for (var i = 0; i < 20000; i++) {
                ascii.Add($"The Quick Brown Fox {i} Jumps Over The Lazy Dog");
                mixed.Add($"Привет Мир {i} Καλημερα Κοσμε and plain words too");
            }

            long total = 0;
            for (var round = 0; round < 5; round++) {
                foreach (var text in ascii) {
                    if (text.ToUpperInvariant() != text) { total++; }
                    total += text.ToLowerInvariant().Length;
                }

                foreach (var text in mixed) {
                    if (text.ToUpperInvariant() != text) { total++; }
                    total += text.ToLowerInvariant().Length;
                }
            }

            return total.ToString();
        }
    }
}
