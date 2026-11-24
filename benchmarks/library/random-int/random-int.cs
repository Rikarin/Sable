// Port of random-int.sb.
[Benchmark("library/random-int")]
static class LibraryRandomInt {
    public static string Run() {
        checked {
            var random = new Random();
            long total = 0;
            for (var i = 0; i < 20000000; i++) {
                total += random.NextInt64(0, 1000);
            }

            return $"{(total + 100000000) / 200000000}";
        }
    }
}
