using System.Buffers.Binary;
using System.Security.Cryptography;

// Port of aes-cbc-large.sb.
[Benchmark("library/aes-cbc-large")]
static class LibraryAesCbcLarge {
    public static string Run() {
        checked {
            var key = new byte[32];
            Array.Fill(key, (byte)7);
            using var cipher = Aes.Create();
            cipher.Key = key;
            var text = new byte[65536];
            Array.Fill(text, (byte)1);
            var iv = new byte[16];
            var total = 0;
            for (var i = 0; i < 200; i++) {
                BinaryPrimitives.WriteUInt64LittleEndian(iv, (ulong)i);
                var ciphertext = cipher.EncryptCbc(text, iv);
                total += ciphertext[0];
                text = cipher.DecryptCbc(ciphertext, iv);
            }

            return $"{total + text[65535]}";
        }
    }
}
