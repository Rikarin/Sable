using System.IO.Compression;


[Benchmark("library/brotli-compress")]
static class LibraryBrotliCompress {
    public static Func<string> Setup() {
        var text = CompressionSample.Text();
        return () => Run(text);
    }

    static string Run(byte[] text) {
        long total = 0;
        for (var round = 0; round < 3; round++) {
            total += CompressionSample.Compressed(text, static packed => new BrotliStream(packed, new BrotliCompressionOptions { Quality = 4 }, true)).Length;
        }

        total += CompressionSample.Compressed(text, static packed => new BrotliStream(packed, new BrotliCompressionOptions { Quality = 1 }, true)).Length;
        return total > text.Length ? $"{text.Length}" : "nothing";
    }
}
