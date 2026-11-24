// Port of format-int.sb.
[Benchmark("library/format-int")]
static class LibraryFormatInt {
    public static string Run() {
        checked {
            long x = 12345;
            long characters = 0;
            for (long i = 0; i < 1000000; i++) {
                x = (x * 1103515245 + 12345) % 2147483648;
                characters += (x * x).ToString().Length + i.ToString().Length;
            }

            return characters.ToString();
        }
    }
}
