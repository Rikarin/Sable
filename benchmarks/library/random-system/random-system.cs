// Port of random-system.sb.
[Benchmark("library/random-system")]
static class LibraryRandomSystem {
    public static string Run() {
        checked {
            var bytes = new byte[65536];
            for (var i = 0; i < 1000; i++) {
                System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            }

            var total = 0;
            foreach (var value in bytes) {
                total += value;
            }

            return $"{(total / 65536 + 16) / 32}";
        }
    }
}
