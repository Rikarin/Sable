using System.IO.Enumeration;

// Port of directory-walk.sb, with FileSystemEnumerable, which gives an
// entry's kind without asking the system again.
[Benchmark("library/directory-walk")]
static class LibraryDirectoryWalk {
    static string Root => Path.GetFullPath("directory-walk-dotnet");

    public static Func<string> Setup() {
        checked {
            var root = Root;
            for (var i = 0; i < 10; i++) {
                for (var j = 0; j < 10; j++) {
                    var directory = $"{root}/branch {i}/leaf {j}";
                    Directory.CreateDirectory(directory);
                    for (var k = 0; k < 20; k++) {
                        File.WriteAllBytes($"{directory}/file {k}.txt", []);
                    }
                }
            }

            return () => Run(root);
        }
    }

    public static void Cleanup() => Directory.Delete(Root, recursive: true);

    static string Run(string root) {
        checked {
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

            return $"{files} {directories} {characters}";
        }
    }
}
