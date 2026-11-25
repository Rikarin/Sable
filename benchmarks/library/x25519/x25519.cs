using System.Security.Cryptography;

// 2000 agreements.
[Benchmark("library/x25519")]
static class LibraryX25519 {
    public static Func<string> Setup() {
        checked {
            var mine = X25519DiffieHellman.GenerateKey();
            var yours = X25519DiffieHellman.GenerateKey();
            var theirs = X25519DiffieHellman.ImportPublicKey(yours.ExportPublicKey());
            return () => Run(mine, theirs);
        }
    }

    static string Run(X25519DiffieHellman mine, X25519DiffieHellman theirs) {
        checked {
            var count = 0;
            for (var round = 0; round < 2000; round++) {
                count += mine.DeriveRawSecretAgreement(theirs).Length;
            }

            return $"{count}";
        }
    }
}
