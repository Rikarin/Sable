using System.Security.Cryptography;

// 200 agreements.
[Benchmark("library/ecdh-p384")]
static class LibraryEcdhP384 {
    public static Func<string> Setup() {
        checked {
            var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
            var yours = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
            var theirs = yours.PublicKey;
            return () => Run(mine, theirs);
        }
    }

    static string Run(ECDiffieHellman mine, ECDiffieHellmanPublicKey theirs) {
        checked {
            var count = 0;
            for (var round = 0; round < 200; round++) {
                count += mine.DeriveRawSecretAgreement(theirs).Length;
            }

            return $"{count}";
        }
    }
}
