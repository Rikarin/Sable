using System.Security.Cryptography;

// 1000 agreements.
[Benchmark("library/ecdh-p256")]
static class LibraryEcdhP256 {
    public static string Run() {
        checked {
            using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var yours = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            using var theirs = yours.PublicKey;
            var count = 0;
            for (var round = 0; round < 1000; round++) {
                count += mine.DeriveRawSecretAgreement(theirs).Length;
            }

            return $"{count}";
        }
    }
}
