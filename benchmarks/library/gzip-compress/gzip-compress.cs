using System.IO.Compression;


[Benchmark("library/gzip-compress")]
static class LibraryGzipCompress {
    public static Func<string> Setup() {
        var text = CompressionSample.Text();
        return () => Run(text);
    }

    static string Run(byte[] text) {
        long total = 0;
        for (var round = 0; round < 2; round++) {
            total += CompressionSample.Compressed(text, static packed => new GZipStream(packed, new ZLibCompressionOptions { CompressionLevel = 6 }, true)).Length;
        }

        total += CompressionSample.Compressed(text, static packed => new GZipStream(packed, new ZLibCompressionOptions { CompressionLevel = 1 }, true)).Length;
        return total > text.Length ? $"{text.Length}" : "nothing";
    }
}
