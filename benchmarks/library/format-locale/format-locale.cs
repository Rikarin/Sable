// Port of format-locale.sb.
[Benchmark("library/format-locale")]
static class LibraryFormatLocale {
    public static string Run() {
        var german = new System.Globalization.CultureInfo("de-DE");
        long total = 0;
        var sum = 0.0;
        for (var i = 0; i < 20000; i++) {
            var text = (i * 98.25).ToString("N2", german);
            total += text.Length;
            sum += double.Parse(text, System.Globalization.NumberStyles.Number, german);
        }

        return $"{total} {sum.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
