using System.Security.Cryptography;

// A megabyte two hundred times, then messages of 64 bytes two hundred thousand.
[Benchmark("library/hmac-sha256")]
static class LibraryHmacSha256 {
    public static string Run() {
        checked {
            var key = Sample(32);
            var big = Sample(1_048_576);
            long check = 0;
            for (var round = 0; round < 200; round++) {
                check += HMACSHA256.HashData(key, big)[round % 32];
            }

            var small = Sample(64);
            for (var round = 0; round < 200_000; round++) {
                var digest = HMACSHA256.HashData(key, small);
                small[round % 64] = digest[round % 32];
            }

            long total = 0;
            foreach (var b in small) {
                total = total * 31 % 1_000_003 + b;
            }

            return $"{check} {total}";
        }
    }

    static byte[] Sample(int count) {
        var bytes = new byte[count];
        var state = 1;
        for (var i = 0; i < count; i++) {
            state = (state * 75 + 74) % 65537;
            bytes[i] = (byte)(state & 0xFF);
        }

        return bytes;
    }
}
