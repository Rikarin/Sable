using System.IO.Enumeration;

// Port of directory-walk.sb, with FileSystemEnumerable, which gives an
// entry's kind without asking the system again.
[Benchmark("library/directory-walk")]
static class LibraryDirectoryWalk {
    public static string Run() {
        checked {
            var root = Path.GetFullPath("directory-walk-dotnet");
            for (var i = 0; i < 10; i++) {
                for (var j = 0; j < 10; j++) {
                    var directory = $"{root}/branch {i}/leaf {j}";
                    Directory.CreateDirectory(directory);
                    for (var k = 0; k < 20; k++) {
                        File.WriteAllBytes($"{directory}/file {k}.txt", []);
                    }
                }
            }

            long files = 0;
            long directories = 0;
            long characters = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
            for (var round = 0; round < 20; round++) {
                var entries = new FileSystemEnumerable<(string Path, bool IsDirectory)>(
                    root,
                    static (ref FileSystemEntry entry) => (entry.ToSpecifiedFullPath(), entry.IsDirectory),
                    options);
                foreach (var (path, isDirectory) in entries) {
                    if (isDirectory) {
                        directories++;
                    } else {
                        files++;
                    }

                    characters += path.Length - root.Length;
                }
            }

            Directory.Delete(root, recursive: true);
            return $"{files} {directories} {characters}";
        }
    }
}
