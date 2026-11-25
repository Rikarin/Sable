// A BigInteger of 10^5 digits (3^209590) in decimal, five times over.
[Benchmark("library/bigint-tostring")]
static class LibraryBigintTostring {
    public static Func<string> Setup() {
        checked {
            var a = System.Numerics.BigInteger.Pow(3, 209590);
            return () => Run(a);
        }
    }

    static string Run(System.Numerics.BigInteger a) {
        checked {
            long total = 0;
            var text = "";
            for (var i = 0; i < 5; i++) {
                text = (a + i).ToString();
                total += text.Length;
            }

            return $"{total} {text[..12]} {text[^12..]}";
        }
    }
}
