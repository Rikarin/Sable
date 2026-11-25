using System.Buffers.Binary;
using System.Security.Cryptography;

// Port of chacha20-poly1305.sb.
[Benchmark("library/chacha20-poly1305", 64, 100_000)]
[Benchmark("library/chacha20-poly1305", 65536, 200)]
static class LibraryChacha20Poly1305 {
    public static Func<string> Setup(int size, int rounds) {
        checked {
            var key = new byte[32];
            Array.Fill(key, (byte)7);
            var cipher = new ChaCha20Poly1305(key);
            return () => Run(cipher, size, rounds);
        }
    }

    static string Run(ChaCha20Poly1305 cipher, int size, int rounds) {
        checked {
            var text = new byte[size];
            Array.Fill(text, (byte)1);
            var nonce = new byte[12];
            var tag = new byte[16];
            var total = 0;
            for (var i = 0; i < rounds; i++) {
                BinaryPrimitives.WriteUInt64LittleEndian(nonce, (ulong)i);
                cipher.Encrypt(nonce, text, text, tag);
                total += tag[0];
                cipher.Decrypt(nonce, text, tag, text);
            }

            return $"{total + text[size - 1]}";
        }
    }
}
