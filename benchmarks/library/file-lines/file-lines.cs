// Port of file-lines.sb.
[Benchmark("library/file-lines")]
static class LibraryFileLines {
    public static Func<string> Setup() {
        checked {
            var lines = new List<string>();
            for (var i = 0; i < 300000; i++) {
                lines.Add($"line {i} of the file, with some more text after it\n");
            }
            return () => Run(lines);
        }
    }

    static string Run(List<string> lines) {
        checked {
            File.WriteAllText("file-lines-dotnet.txt", string.Concat(lines));
            long count = 0;
            long characters = 0;
            foreach (var line in File.ReadLines("file-lines-dotnet.txt")) {
                count++;
                characters += line.Length;
            }

            File.Delete("file-lines-dotnet.txt");
            return $"{count} {characters}";
        }
    }
}
