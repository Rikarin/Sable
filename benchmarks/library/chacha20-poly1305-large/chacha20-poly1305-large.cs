using System.Buffers.Binary;
using System.Security.Cryptography;

// Port of chacha20-poly1305-large.sb.
[Benchmark("library/chacha20-poly1305-large")]
static class LibraryChacha20Poly1305Large {
    public static string Run() {
        checked {
            var key = new byte[32];
            Array.Fill(key, (byte)7);
            using var cipher = new ChaCha20Poly1305(key);
            var text = new byte[65536];
            Array.Fill(text, (byte)1);
            var nonce = new byte[12];
            var tag = new byte[16];
            var total = 0;
            for (var i = 0; i < 200; i++) {
                BinaryPrimitives.WriteUInt64LittleEndian(nonce, (ulong)i);
                cipher.Encrypt(nonce, text, text, tag);
                total += tag[0];
                cipher.Decrypt(nonce, text, tag, text);
            }

            return $"{total + text[65535]}";
        }
    }
}
