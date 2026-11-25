using System.Security.Cryptography;

// 1000 agreements.
[Benchmark("library/ecdh-p256")]
static class LibraryEcdhP256 {
    public static Func<string> Setup() {
        checked {
            var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var yours = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var theirs = yours.PublicKey;
            return () => Run(mine, theirs);
        }
    }

    static string Run(ECDiffieHellman mine, ECDiffieHellmanPublicKey theirs) {
        checked {
            var count = 0;
            for (var round = 0; round < 1000; round++) {
                count += mine.DeriveRawSecretAgreement(theirs).Length;
            }

            return $"{count}";
        }
    }
}
