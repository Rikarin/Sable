using System.Security.Cryptography;

// Port of secure-random.sb.
[Benchmark("library/secure-random")]
static class LibrarySecureRandom {
    public static string Run() {
        checked {
            var bytes = new byte[65536];
            for (var i = 0; i < 200; i++) {
                RandomNumberGenerator.Fill(bytes);
            }

            var key = new byte[16];
            for (var i = 0; i < 200_000; i++) {
                RandomNumberGenerator.Fill(key);
                bytes[i % 65536] = key[i % 16];
            }

            var total = 0;
            foreach (var value in bytes) {
                total += value;
            }

            return $"{(total / 65536 + 16) / 32}";
        }
    }
}
