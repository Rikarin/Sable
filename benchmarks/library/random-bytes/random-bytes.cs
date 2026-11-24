// Port of random-bytes.sb.
[Benchmark("library/random-bytes")]
static class LibraryRandomBytes {
    public static string Run() {
        checked {
            var random = new Random();
            var bytes = new byte[65536];
            for (var i = 0; i < 1000; i++) {
                random.NextBytes(bytes);
            }

            var total = 0;
            foreach (var value in bytes) {
                total += value;
            }

            return $"{(total / 65536 + 16) / 32}";
        }
    }
}
