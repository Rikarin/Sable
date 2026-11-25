using System.Buffers.Binary;
using System.Security.Cryptography;

// Port of aes-gcm-large.sb.
[Benchmark("library/aes-gcm-large")]
static class LibraryAesGcmLarge {
    public static string Run() {
        checked {
            var key = new byte[32];
            Array.Fill(key, (byte)7);
            using var cipher = new AesGcm(key, 16);
            var text = new byte[65536];
            Array.Fill(text, (byte)1);
            var nonce = new byte[12];
            var tag = new byte[16];
            var total = 0;
            for (var i = 0; i < 600; i++) {
                BinaryPrimitives.WriteUInt64LittleEndian(nonce, (ulong)i);
                cipher.Encrypt(nonce, text, text, tag);
                total += tag[0];
                cipher.Decrypt(nonce, text, tag, text);
            }

            return $"{total + text[65535]}";
        }
    }
}
