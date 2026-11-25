// Port of file-metadata.sb.
[Benchmark("library/file-metadata")]
static class LibraryFileMetadata {
    public static string Run() {
        checked {
            var root = Path.GetFullPath("file-metadata-dotnet");
            Directory.CreateDirectory(root);
            var paths = new List<string>();
            for (var i = 0; i < 200; i++) {
                var path = $"{root}/file {i}.txt";
                var bytes = new byte[i];
                Array.Fill(bytes, (byte)1);
                File.WriteAllBytes(path, bytes);
                File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(1700000000 + i).UtcDateTime);
                paths.Add(path);
            }

            long size = 0;
            long seconds = 0;
            long files = 0;
            for (var round = 0; round < 200; round++) {
                foreach (var path in paths) {
                    var info = new FileInfo(path);
                    size += info.Length;
                    seconds += (info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond - 1700000000;
                    if ((info.Attributes & FileAttributes.Directory) == 0) {
                        files++;
                    }

                    if (File.Exists($"{path}.gone")) {
                        files++;
                    }
                }
            }

            Directory.Delete(root, recursive: true);
            return $"{size} {seconds} {files}";
        }
    }
}
