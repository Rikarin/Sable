using System.Security.Cryptography;

// Port of pbkdf2.sb.
[Benchmark("library/pbkdf2")]
static class LibraryPbkdf2 {
    public static string Run() {
        checked {
            var password = new byte[20];
            Array.Fill(password, (byte)7);
            var salt = new byte[16];
            Array.Fill(salt, (byte)9);
            var a = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200_000, HashAlgorithmName.SHA256, 32);
            var b = Rfc2898DeriveBytes.Pbkdf2(password, salt, 200_000, HashAlgorithmName.SHA512, 64);
            long total = 0;
            foreach (var value in a) {
                total = total * 31 % 1_000_003 + value;
            }

            foreach (var value in b) {
                total = total * 31 % 1_000_003 + value;
            }

            return total.ToString();
        }
    }
}
