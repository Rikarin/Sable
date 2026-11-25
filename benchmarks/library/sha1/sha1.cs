using System.Security.Cryptography;

// A megabyte two hundred times, then messages of 64 bytes two hundred thousand.
[Benchmark("library/sha1")]
static class LibrarySha1 {
    public static Func<string> Setup() {
        checked {
            var big = Sample(1_048_576);
            return () => Run(big);
        }
    }

    static string Run(byte[] big) {
        checked {
            long check = 0;
            for (var round = 0; round < 200; round++) {
                check += SHA1.HashData(big)[round % 20];
            }

            var small = Sample(64);
            for (var round = 0; round < 200_000; round++) {
                var digest = SHA1.HashData(small);
                small[round % 64] = digest[round % 20];
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
