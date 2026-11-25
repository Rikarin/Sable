// Port of file-copy.sb.
[Benchmark("library/file-copy")]
static class LibraryFileCopy {
    static byte[] Filled(byte value, int count) {
        var bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    static readonly string Small = Path.GetFullPath("file-copy-dotnet-small.bin");
    static readonly string Large = Path.GetFullPath("file-copy-dotnet-large.bin");
    static readonly string Copy = Path.GetFullPath("file-copy-dotnet-copy.bin");

    public static Func<string> Setup() {
        File.WriteAllBytes(Small, Filled(7, 4000));
        File.WriteAllBytes(Large, Filled(9, 4000000));
        return () => Run(Small, Large, Copy);
    }

    public static void Cleanup() {
        File.Delete(Small);
        File.Delete(Large);
        File.Delete(Copy);
    }

    static string Run(string small, string large, string copy) {
        checked {
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
            return $"{total} {last.Length} {last[^1]}";
        }
    }
}
