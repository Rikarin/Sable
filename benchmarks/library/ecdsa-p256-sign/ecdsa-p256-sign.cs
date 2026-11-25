using System.Security.Cryptography;

// 2000 signatures of a message of 64 bytes, the last of which is checked.
[Benchmark("library/ecdsa-p256-sign")]
static class LibraryEcdsaP256Sign {
    public static Func<string> Setup() {
        checked {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return () => Run(key);
        }
    }

    static string Run(ECDsa key) {
        checked {
            var message = new byte[64];
            Array.Fill(message, (byte)7);
            var signature = key.SignData(message, HashAlgorithmName.SHA256);
            for (var round = 1; round < 2000; round++) {
                message[round % 64] = (byte)(round % 256);
                signature = key.SignData(message, HashAlgorithmName.SHA256);
            }

            return key.VerifyData(message, signature, HashAlgorithmName.SHA256) ? "1" : "0";
        }
    }
}
