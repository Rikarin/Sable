namespace Sable.Driver.Packages;

/// <summary>A package in the cache: where, how big, and when a build last used it.</summary>
public sealed record CachedPackage(string Directory, long Bytes, DateTime LastUsed);

/// <summary>
///     <c>sable cache</c>: what is under <c>$SABLE_HOME</c> that can be made
///     again — fetched packages (<c>cache/</c>) and evaluated manifests
///     (<c>manifests/</c>) — and collecting what has not been used for a while.
/// </summary>
/// <remarks>
///     A cached package is marked used (a <c>.sable-used</c> file, touched at
///     most once a day) whenever a build finds it there; collecting goes by
///     that, so a package every build uses stays, whatever its age. Nothing
///     collected is lost: a build fetches it again, or evaluates the manifest
///     again, checked as the first time.
/// </remarks>
public static class PackageCache {
    const string UsedFile = ".sable-used";

    /// <summary>Notes that a build used the package in <paramref name="directory" />.</summary>
    public static void MarkUsed(string directory) {
        var stamp = Path.Combine(directory, UsedFile);
        try {
            if (!File.Exists(stamp)) {
                File.WriteAllText(stamp, "");
            } else if (File.GetLastWriteTimeUtc(stamp) < DateTime.UtcNow.AddDays(-1)) {
                File.SetLastWriteTimeUtc(stamp, DateTime.UtcNow);
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // A cache that cannot be marked is collected sooner, and fetched again.
        }
    }

    /// <summary>Every package in the cache, oldest use first.</summary>
    public static IReadOnlyList<CachedPackage> List(PackageEnvironment environment) {
        if (!Directory.Exists(environment.CacheDirectory)) {
            return [];
        }

        return [
            ..Directory.GetDirectories(environment.CacheDirectory)
                .Where(static d => File.Exists(Path.Combine(d, SourceStore.ChecksumFile)))
                .Select(static d => new CachedPackage(d, Size(d), LastUsed(d)))
                .OrderBy(static p => p.LastUsed),
        ];
    }

    /// <summary>
    ///     Removes the packages not used, and the evaluated manifests not made
    ///     or read, in <paramref name="maximumAge" /> — and anything a fetch
    ///     interrupted left behind. Returns what was removed and how many bytes.
    /// </summary>
    public static (IReadOnlyList<string> Removed, long Bytes) Collect(PackageEnvironment environment, TimeSpan maximumAge) {
        var cutoff = DateTime.UtcNow - maximumAge;
        var removed = new List<string>();
        long bytes = 0;

        void Remove(string path) {
            var size = Directory.Exists(path) ? Size(path) : new FileInfo(path).Length;
            if (Directory.Exists(path)) {
                Directory.Delete(path, true);
            } else {
                File.Delete(path);
            }

            removed.Add(path);
            bytes += size;
        }

        if (Directory.Exists(environment.CacheDirectory)) {
            foreach (var directory in Directory.GetDirectories(environment.CacheDirectory)) {
                var interrupted = Path.GetFileName(directory).Contains(".tmp-", StringComparison.Ordinal) && Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddHours(-1);
                if (interrupted || File.Exists(Path.Combine(directory, SourceStore.ChecksumFile)) && LastUsed(directory) < cutoff) {
                    Remove(directory);
                }
            }
        }

        var manifests = ManifestsDirectory(environment);
        if (Directory.Exists(manifests)) {
            foreach (var file in Directory.GetFiles(manifests).Where(f => File.GetLastWriteTimeUtc(f) < cutoff)) {
                Remove(file);
            }
        }

        return (removed, bytes);
    }

    /// <summary>Removes the whole cache: every fetched package and evaluated manifest. Returns the bytes freed.</summary>
    public static long Clear(PackageEnvironment environment) {
        long bytes = 0;
        foreach (var directory in (string[])[environment.CacheDirectory, ManifestsDirectory(environment)]) {
            if (Directory.Exists(directory)) {
                bytes += Size(directory);
                Directory.Delete(directory, true);
            }
        }

        return bytes;
    }

    static string ManifestsDirectory(PackageEnvironment environment) => Path.Combine(environment.Home, "manifests");

    static DateTime LastUsed(string directory) {
        var stamp = Path.Combine(directory, UsedFile);
        return File.Exists(stamp) ? File.GetLastWriteTimeUtc(stamp) : File.GetLastWriteTimeUtc(Path.Combine(directory, SourceStore.ChecksumFile));
    }

    static long Size(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(static f => f.Length);

    /// <summary>Bytes as a person reads them: <c>12.3 MB</c>.</summary>
    public static string Describe(long bytes) => bytes switch {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
    };
}
