// Port of int128.sb.
[Benchmark("library/int128")]
static class LibraryInt128 {
    public static string Run() {
        checked {
            var modulus = Int128.Parse("18446744073709551557");
            var large = Int128.Parse("1000000000000000000007");
            var x = Int128.Parse("12345678901234567890");
            Int128 total = 0;
            for (var i = 0; i < 5000000; i++) {
                x = (x * 6364136223846793005 + 1442695040888963407) % modulus;
                total = (total + x / (i + 1)) % large;
            }

            return $"{x} {total}";
        }
    }
}
