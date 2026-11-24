// Port of format-float.sb.
[Benchmark("library/format-float")]
static class LibraryFormatFloat {
    public static string Run() {
        checked {
            long x = 12345;
            long characters = 0;
            for (var i = 0; i < 200000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                var value = (double)(x * 2 + 1) / 4096.0 / 3.0 + 0.5;
                characters += value.ToString().Length;
            }

            return characters.ToString();
        }
    }
}
