using System.Buffers.Binary;
using System.Security.Cryptography;

// Port of aes-cbc.sb.
[Benchmark("library/aes-cbc", 64, 100_000)]
[Benchmark("library/aes-cbc", 65536, 200)]
static class LibraryAesCbc {
    public static Func<string> Setup(int size, int rounds) {
        checked {
            var key = new byte[32];
            Array.Fill(key, (byte)7);
            var cipher = Aes.Create();
            cipher.Key = key;
            return () => Run(cipher, size, rounds);
        }
    }

    static string Run(Aes cipher, int size, int rounds) {
        checked {
            var text = new byte[size];
            Array.Fill(text, (byte)1);
            var iv = new byte[16];
            var total = 0;
            for (var i = 0; i < rounds; i++) {
                BinaryPrimitives.WriteUInt64LittleEndian(iv, (ulong)i);
                var ciphertext = cipher.EncryptCbc(text, iv);
                total += ciphertext[0];
                text = cipher.DecryptCbc(ciphertext, iv);
            }

            return $"{total + text[size - 1]}";
        }
    }
}
