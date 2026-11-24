// Two BigIntegers of about 10^5 decimal digits (3^209590 and 7^118330),
// multiplied ten times over.
[Benchmark("library/bigint-multiply")]
static class LibraryBigintMultiply {
    public static string Run() {
        checked {
            var a = System.Numerics.BigInteger.Pow(3, 209590);
            var b = System.Numerics.BigInteger.Pow(7, 118330);
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
