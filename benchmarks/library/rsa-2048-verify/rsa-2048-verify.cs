using System.Security.Cryptography;

// A signature checked 3000 times, every fourth of another message.
[Benchmark("library/rsa-2048-verify")]
static class LibraryRsa2048Verify {
    public static string Run() {
        checked {
            using var key = RSA.Create(2048);
            var message = new byte[64];
            Array.Fill(message, (byte)7);
            var other = new byte[64];
            Array.Fill(other, (byte)8);
            var signature = key.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var valid = 0;
            for (var round = 0; round < 3000; round++) {
                if (key.VerifyData(round % 4 == 3 ? other : message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) {
                    valid++;
                }
            }

            return $"{valid}";
        }
    }
}
