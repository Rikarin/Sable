// Port of file-copy.sb.
[Benchmark("library/file-copy")]
static class LibraryFileCopy {
    static byte[] Filled(byte value, int count) {
        var bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    public static string Run() {
        checked {
            var small = Path.GetFullPath("file-copy-dotnet-small.bin");
            var large = Path.GetFullPath("file-copy-dotnet-large.bin");
            var copy = Path.GetFullPath("file-copy-dotnet-copy.bin");
            File.WriteAllBytes(small, Filled(7, 4000));
            File.WriteAllBytes(large, Filled(9, 4000000));
            long total = 0;
            for (var i = 0; i < 1000; i++) {
                File.Copy(small, copy, overwrite: true);
                total += new FileInfo(copy).Length;
            }

            for (var i = 0; i < 10; i++) {
                File.Copy(large, copy, overwrite: true);
                total += new FileInfo(copy).Length;
            }

            var last = File.ReadAllBytes(copy);
            File.Delete(small);
            File.Delete(large);
            File.Delete(copy);
            return $"{total} {last.Length} {last[^1]}";
        }
    }
}
