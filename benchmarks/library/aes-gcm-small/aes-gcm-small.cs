using System.Buffers.Binary;
using System.Security.Cryptography;

// Port of aes-gcm-small.sb.
[Benchmark("library/aes-gcm-small")]
static class LibraryAesGcmSmall {
    public static string Run() {
        checked {
            var key = new byte[32];
            Array.Fill(key, (byte)7);
            using var cipher = new AesGcm(key, 16);
            var text = new byte[64];
            Array.Fill(text, (byte)1);
            var nonce = new byte[12];
            var tag = new byte[16];
            var total = 0;
            for (var i = 0; i < 100_000; i++) {
                BinaryPrimitives.WriteUInt64LittleEndian(nonce, (ulong)i);
                cipher.Encrypt(nonce, text, text, tag);
                total += tag[0];
                cipher.Decrypt(nonce, text, tag, text);
            }

            return $"{total + text[63]}";
        }
    }
}
