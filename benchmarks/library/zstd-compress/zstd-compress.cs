using System.IO.Compression;


[Benchmark("library/zstd-compress")]
static class LibraryZstdCompress {
    public static Func<string> Setup() {
        var text = CompressionSample.Text();
        return () => Run(text);
    }

    static string Run(byte[] text) {
        long total = 0;
        for (var round = 0; round < 6; round++) {
            total += CompressionSample.Compressed(text, static packed => new ZstandardStream(packed, new ZstandardCompressionOptions { Quality = 3 }, true)).Length;
        }

        total += CompressionSample.Compressed(text, static packed => new ZstandardStream(packed, new ZstandardCompressionOptions { Quality = 1 }, true)).Length;
        return total > text.Length ? $"{text.Length}" : "nothing";
    }
}
