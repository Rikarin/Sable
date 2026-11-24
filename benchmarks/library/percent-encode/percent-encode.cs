// Port of percent-encode.sb.
[Benchmark("library/percent-encode")]
static class LibraryPercentEncode {
    public static string Run() {
        checked {
            long total = 0;
            for (var i = 0; i < 100000; i++) {
                var text = $"name {i} & value = 50% / größe ✓";
                var encoded = Uri.EscapeDataString(text);
                total += encoded.Length;
                if (Uri.UnescapeDataString(encoded) == text) { total++; }
            }

            return total.ToString();
        }
    }
}
