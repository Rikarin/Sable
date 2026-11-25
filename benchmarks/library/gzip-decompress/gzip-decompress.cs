using System.IO.Compression;


[Benchmark("library/gzip-decompress")]
static class LibraryGzipDecompress {
    public static string Run() {
        var text = CompressionSample.Text();
        var packed = CompressionSample.Compressed(text, static packed => new GZipStream(packed, new ZLibCompressionOptions { CompressionLevel = 6 }, true));
        var into = new byte[text.Length + 1];
        long total = 0;
        for (var round = 0; round < 20; round++) {
            total += CompressionSample.Decompressed(new GZipStream(new MemoryStream(packed), CompressionMode.Decompress), into);
        }

        return $"{total}";
    }
}
