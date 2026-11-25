using System.Formats.Tar;

// Five thousand small entries written to a tar archive in memory and read again, ten times.
[Benchmark("library/tar")]
static class LibraryTar {
    public static string Run() {
        checked {
            var text = new byte[3000];
            Array.Fill(text, (byte)0x61);
            long total = 0;
            for (var round = 0; round < 10; round++) {
                using var memory = new MemoryStream();
                using (var writer = new TarWriter(memory, TarEntryFormat.Pax, true)) {
                    for (var i = 0; i < 5000; i++) {
                        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"directory/entry {i}.txt") {
                            DataStream = new MemoryStream(text, 0, i % 3000),
                        });
                    }
                }

                memory.Position = 0;
                using var reader = new TarReader(memory);
                while (reader.GetNextEntry() is { } entry) {
                    using var bytes = new MemoryStream();
                    entry.DataStream?.CopyTo(bytes);
                    total += entry.Length + bytes.Length;
                }
            }

            return $"{total}";
        }
    }
}
