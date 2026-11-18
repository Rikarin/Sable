using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using Sable.Driver.Packages;

namespace Sable.Driver;

/// <summary>An installed toolchain: its version and the directory holding its <c>sable</c>.</summary>
public sealed record InstalledToolchain(string Version, string Directory);

/// <summary>
///     <c>sable toolchain</c> (docs/05 §5.1): toolchains installed side by side
///     under <c>$SABLE_HOME/toolchains/&lt;version&gt;/</c>, and a workspace's pin
///     on one in a <c>sable-toolchain</c> file at its root — so that the
///     toolchain version, which with the manifest and the lock file determines
///     the artifact, is written down and used.
/// </summary>
/// <remarks>
///     <para>
///         A pinned workspace is built by the pinned toolchain: the <c>sable</c>
///         on the path hands the command to it, as rustup's proxies do. A pin on
///         a toolchain that is not installed is an error naming the command that
///         installs it, never a silent build with another.
///     </para>
///     <para>
///         There is no release server yet, so <c>install</c> takes a toolchain
///         from a build of it: a directory with <c>sable</c> (or
///         <c>sable.dll</c>) in it, or a <c>.tar</c> or <c>.tar.gz</c> of one.
///     </para>
/// </remarks>
public static class Toolchains {
    /// <summary>The file at a workspace's root naming the toolchain it is built with.</summary>
    public const string PinFile = "sable-toolchain";

    /// <summary>Set in the environment of a toolchain this one hands a command to, so that it does not hand it on again.</summary>
    const string ActiveVariable = "SABLE_TOOLCHAIN_ACTIVE";

    /// <summary>The running toolchain's version: the driver's, without its build metadata.</summary>
    public static string RunningVersion {
        get {
            var informational = typeof(Toolchains).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
    }

    /// <summary>The running toolchain's build: the commit it was built from, when the build recorded it, else its assembly's identity.</summary>
    public static string RunningBuild {
        get {
            var informational = typeof(Toolchains).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 && informational.Length > plus + 1
                ? informational[(plus + 1)..][..Math.Min(12, informational.Length - plus - 1)]
                : typeof(Toolchains).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
        }
    }

    public static string Directory(string home) => Path.Combine(home, "toolchains");

    /// <summary>Every toolchain installed under <paramref name="home" />, by version.</summary>
    public static IReadOnlyList<InstalledToolchain> Installed(string home) {
        var root = Directory(home);
        if (!System.IO.Directory.Exists(root)) {
            return [];
        }

        return [
            ..System.IO.Directory.GetDirectories(root)
                .Where(static d => Entry(d) is not null)
                .Select(static d => new InstalledToolchain(Path.GetFileName(d), d))
                .OrderBy(static t => SemVersion.TryParse(t.Version, out var v, out _) ? v : null)
                .ThenBy(static t => t.Version, StringComparer.Ordinal),
        ];
    }

    /// <summary>The pin in effect in <paramref name="directory" />: the nearest <c>sable-toolchain</c> at or above it, with where it is.</summary>
    public static (string Version, string File)? Pin(string directory) {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent) {
            var file = Path.Combine(current.FullName, PinFile);
            if (File.Exists(file)) {
                var version = File.ReadAllLines(file).Select(static l => l.Trim()).FirstOrDefault(static l => l.Length > 0 && !l.StartsWith('#'));
                return version is null ? null : (version, file);
            }
        }

        return null;
    }

    /// <summary>
    ///     Pins the workspace in <paramref name="directory" /> (or the directory
    ///     itself, outside one) to <paramref name="version" />; returns the file written.
    /// </summary>
    public static string WritePin(string directory, string version) {
        CheckVersion(version);
        var root = Workspace.FindManifestDirectory(directory) is null ? Path.GetFullPath(directory) : Workspace.Load(directory).Root;
        var file = Path.Combine(root, PinFile);
        File.WriteAllText(file, version + "\n");
        return file;
    }

    /// <summary>
    ///     Installs the toolchain at <paramref name="source" /> — a build's
    ///     directory, or a <c>.tar</c> / <c>.tar.gz</c> of one — as
    ///     <paramref name="version" />; returns where.
    /// </summary>
    public static string Install(string home, string version, string source) {
        CheckVersion(version);
        var target = Path.Combine(Directory(home), version);
        if (System.IO.Directory.Exists(target)) {
            throw new PackageException($"toolchain {version} is already installed at {target}; 'sable toolchain uninstall {version}' first to replace it");
        }

        var temporary = target + $".tmp-{Guid.NewGuid():N}";
        System.IO.Directory.CreateDirectory(temporary);
        try {
            if (System.IO.Directory.Exists(source)) {
                Copy(source, temporary);
            } else if (File.Exists(source) && (source.EndsWith(".tar", StringComparison.Ordinal) || source.EndsWith(".tar.gz", StringComparison.Ordinal) || source.EndsWith(".tgz", StringComparison.Ordinal))) {
                using var file = File.OpenRead(source);
                using Stream stream = source.EndsWith(".tar", StringComparison.Ordinal) ? file : new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(stream, temporary, overwriteFiles: false);
            } else {
                throw new PackageException($"'{source}' is not a toolchain: give the directory of a build of sable, or a .tar or .tar.gz of one");
            }

            // An archive may hold the toolchain one directory down.
            var root = Entry(temporary) is not null ? temporary
                : System.IO.Directory.GetDirectories(temporary) is [var only] && Entry(only) is not null ? only
                : throw new PackageException($"'{source}' has no sable or sable.dll in it, so it is not a toolchain");
            System.IO.Directory.CreateDirectory(Directory(home));
            System.IO.Directory.Move(root, target);
            return target;
        } finally {
            if (System.IO.Directory.Exists(temporary)) {
                System.IO.Directory.Delete(temporary, true);
            }
        }
    }

    /// <summary>Removes an installed toolchain.</summary>
    public static void Uninstall(string home, string version) {
        var target = Path.Combine(Directory(home), version);
        if (!System.IO.Directory.Exists(target)) {
            throw new PackageException($"toolchain {version} is not installed");
        }

        System.IO.Directory.Delete(target, true);
    }

    /// <summary>
    ///     When the current directory is pinned to another toolchain: runs the
    ///     command with it and returns its exit code. Null when this toolchain
    ///     is the one to use.
    /// </summary>
    public static int? Redirect(IReadOnlyList<string> arguments) {
        if (Environment.GetEnvironmentVariable(ActiveVariable) is { Length: > 0 } || Pin(System.IO.Directory.GetCurrentDirectory()) is not var (version, file)) {
            return null;
        }

        if (version == RunningVersion) {
            return null;
        }

        var home = PackageEnvironment.HomeFromProcess;
        var installed = Installed(home).FirstOrDefault(t => t.Version == version);
        if (installed is null || Entry(installed.Directory) is not { } entry) {
            Console.Error.WriteLine($"error: {file} pins toolchain {version}, which is not installed (this is {RunningVersion}); install it with 'sable toolchain install {version} --from <build>', or change the pin with 'sable toolchain pin {RunningVersion}'");
            return 1;
        }

        var start = entry.EndsWith(".dll", StringComparison.Ordinal) ? new ProcessStartInfo("dotnet") { ArgumentList = { entry } } : new ProcessStartInfo(entry);
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        start.Environment[ActiveVariable] = version;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not run {entry}");
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>The program that runs a toolchain in <paramref name="directory" />: its native <c>sable</c>, else its <c>sable.dll</c>.</summary>
    static string? Entry(string directory) {
        foreach (var name in (string[])[OperatingSystem.IsWindows() ? "sable.exe" : "sable", "sable.dll"]) {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) {
                return path;
            }
        }

        return null;
    }

    static void CheckVersion(string version) {
        if (version.Length == 0 || version.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+' or '_')) || version.StartsWith('.')) {
            throw new PackageException($"'{version}' is not a toolchain version: write it as 'sable --version' prints it, such as 0.5.0");
        }
    }

    static void Copy(string source, string target) {
        System.IO.Directory.CreateDirectory(target);
        foreach (var file in System.IO.Directory.GetFiles(source)) {
            var copied = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, copied);
            if (!OperatingSystem.IsWindows()) {
                File.SetUnixFileMode(copied, File.GetUnixFileMode(file));
            }
        }

        foreach (var directory in System.IO.Directory.GetDirectories(source)) {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
