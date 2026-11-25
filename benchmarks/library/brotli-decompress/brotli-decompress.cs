using System.IO.Compression;


[Benchmark("library/brotli-decompress")]
static class LibraryBrotliDecompress {
    public static string Run() {
        var text = CompressionSample.Text();
        var packed = CompressionSample.Compressed(text, static packed => new BrotliStream(packed, new BrotliCompressionOptions { Quality = 4 }, true));
        var into = new byte[text.Length + 1];
        long total = 0;
        for (var round = 0; round < 15; round++) {
            total += CompressionSample.Decompressed(new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress), into);
        }

        return $"{total}";
    }
}
