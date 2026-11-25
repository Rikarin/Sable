// A BigInteger of 10^5 decimal digits divided by one of half as many, ten
// times over, for the quotient and the remainder at once.
[Benchmark("library/bigint-divide")]
static class LibraryBigintDivide {
    public static Func<string> Setup() {
        checked {
            var a = System.Numerics.BigInteger.Pow(3, 209590);
            var b = System.Numerics.BigInteger.Pow(7, 59165);
            return () => Run(a, b);
        }
    }

    static string Run(System.Numerics.BigInteger a, System.Numerics.BigInteger b) {
        checked {
            long bits = 0;
            ulong low = 0;
            for (var i = 0; i < 10; i++) {
                var (quotient, remainder) = System.Numerics.BigInteger.DivRem(a + i, b);
                bits += (long)quotient.GetBitLength();
                low ^= (ulong)(remainder & ulong.MaxValue);
            }

            return $"{bits} {low}";
        }
    }
}
