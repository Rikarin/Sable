using System.Security.Cryptography;

// 200 agreements.
[Benchmark("library/ecdh-p384")]
static class LibraryEcdhP384 {
    public static string Run() {
        checked {
            using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
            using var yours = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
            using var theirs = yours.PublicKey;
            var count = 0;
            for (var round = 0; round < 200; round++) {
                count += mine.DeriveRawSecretAgreement(theirs).Length;
            }

            return $"{count}";
        }
    }
}
