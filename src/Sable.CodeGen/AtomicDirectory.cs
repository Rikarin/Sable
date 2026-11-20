namespace Sable.CodeGen;

/// <summary>The runtime compiled for one set of options: its object, the archive of it, and how compiling them went.</summary>
public sealed record RuntimeBuild(string Object, string Archive, NativeBuildResult Result);

/// <summary>
///     A directory of build products that is there whole or not at all, so
///     that builds running at once — and the language server — can share it:
///     it is made under a temporary name beside where it goes, then renamed
///     into place in one step.
/// </summary>
public static class AtomicDirectory {
    /// <summary>
    ///     What every temporary directory's name has in it, so that one an
    ///     interrupted build left behind can be told apart and collected.
    /// </summary>
    public const string TemporaryMarker = ".tmp-";

    /// <summary>
    ///     <paramref name="directory" /> as it is when it is there; otherwise
    ///     made by <paramref name="make" /> in a new, empty temporary directory
    ///     and renamed into place when it succeeds. When another build put the
    ///     same directory there first, its is kept and this one thrown away:
    ///     what names the directory is everything it is made from, so theirs
    ///     is as good.
    /// </summary>
    public static NativeBuildResult GetOrCreate(string directory, Func<string, NativeBuildResult> make) {
        if (Directory.Exists(directory)) {
            return new(true, "");
        }

        var temporary = TemporaryFor(directory);
        Directory.CreateDirectory(temporary);
        try {
            var made = make(temporary);
            if (made.Succeeded) {
                Publish(temporary, directory);
            }

            return made;
        } finally {
            if (Directory.Exists(temporary)) {
                Directory.Delete(temporary, true);
            }
        }
    }

    /// <summary>A temporary directory's name beside <paramref name="directory" />: this process's, and no other build's.</summary>
    public static string TemporaryFor(string directory) =>
        Path.TrimEndingDirectorySeparator(directory) + $"{TemporaryMarker}{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <summary>
    ///     Renames <paramref name="temporary" /> to <paramref name="directory" />
    ///     — one step, which a reader sees before or after and never during —
    ///     unless another build put it there first. True when this one did.
    /// </summary>
    public static bool Publish(string temporary, string directory) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(directory))!);
        try {
            Directory.Move(temporary, directory);
            return true;
        } catch (IOException) when (Directory.Exists(directory)) {
            // Another build's got there first.
            return false;
        } catch (UnauthorizedAccessException) when (Directory.Exists(directory)) {
            // On Windows, the same: theirs is in use.
            return false;
        }
    }
}
