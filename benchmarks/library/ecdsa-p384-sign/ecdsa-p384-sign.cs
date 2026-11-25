using System.Security.Cryptography;

// 500 signatures of a message of 64 bytes, the last of which is checked.
[Benchmark("library/ecdsa-p384-sign")]
static class LibraryEcdsaP384Sign {
    public static Func<string> Setup() {
        checked {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            return () => Run(key);
        }
    }

    static string Run(ECDsa key) {
        checked {
            var message = new byte[64];
            Array.Fill(message, (byte)7);
            var signature = key.SignData(message, HashAlgorithmName.SHA384);
            for (var round = 1; round < 500; round++) {
                message[round % 64] = (byte)(round % 256);
                signature = key.SignData(message, HashAlgorithmName.SHA384);
            }

            return key.VerifyData(message, signature, HashAlgorithmName.SHA384) ? "1" : "0";
        }
    }
}
