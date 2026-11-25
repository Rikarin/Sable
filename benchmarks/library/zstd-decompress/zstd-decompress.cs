using System.IO.Compression;


[Benchmark("library/zstd-decompress")]
static class LibraryZstdDecompress {
    public static string Run() {
        var text = CompressionSample.Text();
        var packed = CompressionSample.Compressed(text, static packed => new ZstandardStream(packed, new ZstandardCompressionOptions { Quality = 3 }, true));
        var into = new byte[text.Length + 1];
        long total = 0;
        for (var round = 0; round < 40; round++) {
            total += CompressionSample.Decompressed(new ZstandardStream(new MemoryStream(packed), CompressionMode.Decompress), into);
        }

        return $"{total}";
    }
}
