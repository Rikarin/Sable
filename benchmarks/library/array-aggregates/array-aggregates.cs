using System.Linq;

// Port of array-aggregates.sb: LINQ's Sum, Min and Max, which are vectorised over arrays.
[Benchmark("library/array-aggregates")]
static class LibraryArrayAggregates {
    static long Work(int[] words, long[] longs, byte[] bytes, int rounds) {
        checked {
            long total = 0;
            for (var round = 0; round < rounds; round++) {
                total += words.Sum() + longs.Sum();
                total += words.Min() + (long)words.Max();
                total += longs.Min() + longs.Max();
                total += bytes.Min() + (long)bytes.Max() + round;
            }

            return total;
        }
    }

    public static Func<string> Setup() {
        checked {
            var words = new int[65536];
            var longs = new long[65536];
            var bytes = new byte[65536];
            for (var i = 0; i < 65536; i++) {
                words[i] = (int)((long)i * 7919 % 4001) - 2000;
                longs[i] = (long)i * 104729 % 400001 - 200000;
                bytes[i] = (byte)(i * 31 % 241 + 5);
            }
            return () => Run(words, longs, bytes);
        }
    }

    static string Run(int[] words, long[] longs, byte[] bytes) {
        checked {
            return (Work(words, longs, bytes, 1000) + Work(words[..24], longs[..24], bytes[..24], 200000)).ToString();
        }
    }
}
