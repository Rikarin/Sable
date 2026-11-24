// Port of string-replace.sb.
[Benchmark("library/string-replace")]
static class LibraryStringReplace {
    public static string Run() {
        checked {
            var text = string.Concat(Enumerable.Repeat("the cat sat on the mat and the dog lay on the rug; ", 20000));
            long total = 0;
            for (var round = 0; round < 20; round++) {
                total += text.Replace("the", "every").Length;
                total += text.Replace(" on ", " ").Length;
            }

            return total.ToString();
        }
    }
}
