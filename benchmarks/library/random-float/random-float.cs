// Port of random-float.sb.
[Benchmark("library/random-float")]
static class LibraryRandomFloat {
    public static string Run() {
        var random = new Random();
        var total = 0.0;
        for (var i = 0; i < 20000000; i++) {
            total += random.NextDouble();
        }

        return $"{Math.Round(total / 2000000.0):F1}";
    }
}
