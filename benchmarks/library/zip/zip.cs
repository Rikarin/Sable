using System.IO.Compression;

// Two thousand small entries written to a zip archive in memory and read again, five times.
[Benchmark("library/zip")]
static class LibraryZip {
    public static string Run() {
        checked {
            var text = new byte[2000];
            long state = 1;
            for (var i = 0; i < text.Length; i++) {
                state = (state * 75 + 74) % 65537;
                text[i] = (byte)(0x61 + state % 7);
            }

            long total = 0;
            for (var round = 0; round < 5; round++) {
                using var memory = new MemoryStream();
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true)) {
                    for (var i = 0; i < 2000; i++) {
                        using var entry = archive.CreateEntry($"directory/entry {i}.txt", CompressionLevel.Fastest).Open();
                        entry.Write(text.AsSpan(0, i % 2000));
                    }
                }

                using var read = new ZipArchive(memory, ZipArchiveMode.Read);
                foreach (var entry in read.Entries) {
                    using var bytes = new MemoryStream();
                    using var stream = entry.Open();
                    stream.CopyTo(bytes);
                    total += bytes.Length;
                }
            }

            return $"{total}";
        }
    }
}
