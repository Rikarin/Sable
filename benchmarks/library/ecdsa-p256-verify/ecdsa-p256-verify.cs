using System.Security.Cryptography;

// A signature checked 1000 times, every fourth of another message.
[Benchmark("library/ecdsa-p256-verify")]
static class LibraryEcdsaP256Verify {
    public static string Run() {
        checked {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var message = new byte[64];
            Array.Fill(message, (byte)7);
            var other = new byte[64];
            Array.Fill(other, (byte)8);
            var signature = key.SignData(message, HashAlgorithmName.SHA256);
            var valid = 0;
            for (var round = 0; round < 1000; round++) {
                if (key.VerifyData(round % 4 == 3 ? other : message, signature, HashAlgorithmName.SHA256)) {
                    valid++;
                }
            }

            return $"{valid}";
        }
    }
}
