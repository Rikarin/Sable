using System.Security.Cryptography;

// 100 PSS signatures and 100 of PKCS #1 v1.5, the last of which is checked.
[Benchmark("library/rsa-2048-sign")]
static class LibraryRsa2048Sign {
    public static string Run() {
        checked {
            using var key = RSA.Create(2048);
            var message = new byte[64];
            Array.Fill(message, (byte)7);
            var signature = Array.Empty<byte>();
            for (var round = 0; round < 200; round++) {
                message[round % 64] = (byte)(round % 256);
                signature = key.SignData(message, HashAlgorithmName.SHA256, round % 2 == 0 ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
            }

            return key.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) ? "1" : "0";
        }
    }
}
