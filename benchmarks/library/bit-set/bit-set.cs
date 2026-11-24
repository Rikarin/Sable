using System.Collections;

// Port of bit-set.sb, with BitArray, which has a length of its own and no count.
[Benchmark("library/bit-set")]
static class LibraryBitSet {
    public static string Run() {
        checked {
            const int limit = 5000000;
            var composite = new BitArray(limit);
            var primes = 0;
            for (var i = 2; i < limit; i++) {
                if (!composite[i]) {
                    primes++;
                    for (var j = (long)i * i; j < limit; j += i) {
                        composite[(int)j] = true;
                    }
                }
            }

            var thirds = new BitArray(1000000);
            var fifths = new BitArray(1000000);
            for (var i = 0; i < 1000000; i++) {
                if (i % 3 == 0) { thirds[i] = true; }
                if (i % 5 == 0) { fifths[i] = true; }
            }

            long counted = 0;
            var words = new int[(1000000 + 31) / 32];
            for (var round = 0; round < 100; round++) {
                counted += Count(new BitArray(thirds).And(fifths)) + Count(new BitArray(thirds).Or(fifths));
            }

            return $"{primes} {counted}";

            long Count(BitArray bits) {
                bits.CopyTo(words, 0);
                long count = 0;
                foreach (var word in words) {
                    count += int.PopCount(word);
                }

                return count;
            }
        }
    }
}
