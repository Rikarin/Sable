// Port of array-search.sb.
[Benchmark("library/array-search")]
static class LibraryArraySearch {
    static long Work(ReadOnlySpan<int> words, ReadOnlySpan<int> others, ReadOnlySpan<long> longs, ReadOnlySpan<byte> bytes, int rounds) {
        var total = 0L;
        for (var round = 0; round < rounds; round++) {
            total += Math.Max(0, words.IndexOf(999));
            total += Math.Max(0, words.LastIndexOf(998));
            total += words.Count(7);
            total += words.SequenceEqual(others) ? 1 : 0;
            total += Math.Max(0, longs.IndexOf(999L));
            total += longs.Contains(998L) ? 1 : 0;
            total += Math.Max(0, bytes.IndexOfAny((byte)200, (byte)201, (byte)202));
            total += Math.Max(0, bytes.LastIndexOf((byte)199));
        }

        return total;
    }

    public static Func<string> Setup() {
        var words = new int[65536];
        var longs = new long[65536];
        var bytes = new byte[65536];
        for (var i = 0; i < 65536; i++) {
            words[i] = i % 97;
            longs[i] = i % 97;
            bytes[i] = (byte)(i % 97);
        }

        words[65530] = 999;
        words[3] = 998;
        longs[65530] = 999;
        bytes[65530] = 202;
        bytes[3] = 199;
        var others = (int[])words.Clone();
        return () => Run(words, others, longs, bytes);
    }

    static string Run(int[] words, int[] others, long[] longs, byte[] bytes) {
        return (Work(words, others, longs, bytes, 1000) + Work(words.AsSpan(0, 24), others.AsSpan(0, 24), longs.AsSpan(0, 24), bytes.AsSpan(0, 24), 200000)).ToString();
    }
}
