// Two BigIntegers of about 10^5 decimal digits (3^209590 and 7^118330),
// multiplied ten times over.
[Benchmark("library/bigint-multiply")]
static class LibraryBigintMultiply {
    public static Func<string> Setup() {
        checked {
            var a = System.Numerics.BigInteger.Pow(3, 209590);
            var b = System.Numerics.BigInteger.Pow(7, 118330);
            return () => Run(a, b);
        }
    }

    static string Run(System.Numerics.BigInteger a, System.Numerics.BigInteger b) {
        checked {
            long bits = 0;
            ulong low = 0;
            for (var i = 0; i < 10; i++) {
                var product = a * (b + i);
                bits += (long)product.GetBitLength();
                low ^= (ulong)(product & ulong.MaxValue);
            }

            return $"{bits} {low}";
        }
    }
}
