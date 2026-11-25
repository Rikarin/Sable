// Port of text-search.sb.
[Benchmark("library/text-search")]
static class LibraryTextSearch {
    public static Func<string> Setup() {
        checked {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < 20000; i++) {
                builder.Append("lorem ipsum dolor sit amet, consectetur adipiscing elit, ");
            }

            builder.Append("needle");
            var text = builder.ToString();
            return () => Run(text);
        }
    }

    static string Run(string text) {
        checked {
            long found = 0;
            long pieces = 0;
            for (var round = 0; round < 200; round++) {
                var at = text.IndexOf("needle", StringComparison.Ordinal);
                if (at >= 0) {
                    found += at;
                }

                if (text.IndexOf("haystack", StringComparison.Ordinal) < 0) {
                    found++;
                }

                pieces += text.Split(", ").Length;
            }

            return $"{found} {pieces}";
        }
    }
}
