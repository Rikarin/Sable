using System.Security.Cryptography;

// 2000 agreements.
[Benchmark("library/x25519")]
static class LibraryX25519 {
    public static string Run() {
        checked {
            using var mine = X25519DiffieHellman.GenerateKey();
            using var yours = X25519DiffieHellman.GenerateKey();
            using var theirs = X25519DiffieHellman.ImportPublicKey(yours.ExportPublicKey());
            var count = 0;
            for (var round = 0; round < 2000; round++) {
                count += mine.DeriveRawSecretAgreement(theirs).Length;
            }

            return $"{count}";
        }
    }
}
