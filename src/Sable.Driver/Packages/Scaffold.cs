using System.Text;
using System.Text.RegularExpressions;

namespace Sable.Driver.Packages;

/// <summary><c>sable new</c> and <c>sable init</c>: a package that builds, from nothing.</summary>
public static partial class Scaffold {
    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex Invalid();

    /// <summary>A package name made from a directory's name: lower-case, with anything else turned into '-'.</summary>
    public static string NameFrom(string directoryName) {
        var name = Invalid().Replace(directoryName.ToLowerInvariant(), "-").Trim('-', '_');
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) {
            name = "package-" + name;
        }

        return name.TrimEnd('-');
    }

    /// <summary>The module a package's code is in: its name in PascalCase, <c>my-app</c> → <c>MyApp</c>.</summary>
    public static string ModuleName(string package) {
        var builder = new StringBuilder();
        foreach (var part in LocalPackage.BinaryName(package).Split('-', '_', StringSplitOptions.RemoveEmptyEntries)) {
            builder.Append(char.ToUpperInvariant(part[0])).Append(part[1..]);
        }

        return builder.ToString();
    }

    /// <summary><c>sable new &lt;name&gt;</c>: a new directory holding a new package.</summary>
    public static string New(string parent, string name, bool library, PackageEnvironment environment) {
        var directory = Path.GetFullPath(Path.Combine(parent, LocalPackage.BinaryName(name)));
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) {
            throw new PackageException($"'{directory}' exists already and is not empty; 'sable init' makes a package in an existing directory");
        }

        Write(directory, name, library);
        environment.Error.WriteLine($"     Created {(library ? "library" : "binary")} package `{name}` in {directory}");
        return directory;
    }

    /// <summary><c>sable init</c>: a package in an existing directory, named for it unless a name is given.</summary>
    public static void Init(string directory, string? name, bool library, PackageEnvironment environment) {
        directory = Path.GetFullPath(directory);
        if (File.Exists(Path.Combine(directory, Workspace.ManifestName))) {
            throw new PackageException($"'{directory}' already has a {Workspace.ManifestName}");
        }

        name ??= NameFrom(Path.GetFileName(directory));
        Write(directory, name, library);
        environment.Error.WriteLine($"     Created {(library ? "library" : "binary")} package `{name}` in {directory}");
    }

    static void Write(string directory, string name, bool library) {
        if (!Manifest.IsValidPackageName(name)) {
            throw new PackageException($"'{name}' is not a package name: lower-case letters, digits, '-' and '_', starting with a letter, optionally 'org/name'");
        }

        var source = Path.Combine(directory, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(directory, Workspace.ManifestName), $"""
            [package]
            name = {TomlText.Quote(name)}
            version = "0.1.0"
            edition = "{Manifest.DefaultEdition}"

            [dependencies]

            """);

        var module = ModuleName(name);
        var file = Path.Combine(source, library ? "lib.sab" : "main.sab");
        if (!File.Exists(file)) {
            File.WriteAllText(file, library
                ? $$"""
                    module {{module}}

                    /// The sum of two integers.
                    public fn add(_ a: Int, _ b: Int) -> Int {
                        return a + b
                    }

                    """
                : $$"""
                    module {{module}}

                    import Sable.IO

                    fn main() {
                        print("Hello, world!")
                    }

                    """);
        }

        var ignore = Path.Combine(directory, ".gitignore");
        if (!File.Exists(ignore)) {
            File.WriteAllText(ignore, "/target\n");
        }
    }
}

/// <summary>
///     Edits a manifest as text, so its comments and layout survive: <c>sable
///     add</c> and <c>sable remove</c>. An edit that leaves the manifest
///     invalid is undone.
/// </summary>
public static partial class ManifestEditor {
    [GeneratedRegex(@"^\s*\[\s*([A-Za-z0-9_.""-]+)\s*\]\s*(#.*)?$")]
    private static partial Regex Header();

    [GeneratedRegex(@"^\s*\[")]
    private static partial Regex AnyHeader();

    static bool IsKey(string line, string key) {
        var trimmed = line.TrimStart();
        foreach (var form in new[] { key, $"\"{key}\"", $"'{key}'" }) {
            if (trimmed.StartsWith(form, StringComparison.Ordinal) && trimmed[form.Length..].TrimStart() is ['=', ..] or ['.', ..]) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sets <c>name = value</c> in <c>[section]</c>, replacing what was there.</summary>
    public static void SetDependency(string manifestPath, string section, string name, string value) {
        var original = File.ReadAllText(manifestPath);
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.FindIndex(l => Header().Match(l) is { Success: true } m && m.Groups[1].Value == $"{section}.{name}") >= 0) {
            throw new PackageException($"{manifestPath}: '{name}' is written as a [{section}.{name}] table; edit it by hand");
        }

        var entry = $"{TomlText.Key(name)} = {value}";
        var header = lines.FindIndex(l => Header().Match(l) is { Success: true } m && m.Groups[1].Value == section);
        if (header < 0) {
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) {
                lines.RemoveAt(lines.Count - 1);
            }

            lines.AddRange(["", $"[{section}]", entry, ""]);
        } else {
            var end = lines.FindIndex(header + 1, l => AnyHeader().IsMatch(l));
            end = end < 0 ? lines.Count : end;
            var existing = lines.FindIndex(header + 1, end - header - 1, l => IsKey(l, name));
            if (existing >= 0) {
                lines[existing] = entry;
            } else {
                // After the table's last entry: blank lines and the comments
                // before the next header belong to what follows.
                var last = end - 1;
                while (last > header && (lines[last].Trim().Length == 0 || lines[last].TrimStart().StartsWith('#'))) {
                    last--;
                }

                lines.Insert(last + 1, entry);
            }
        }

        Save(manifestPath, original, string.Join('\n', lines));
    }

    /// <summary>Removes a dependency from whichever dependency tables have it; false when none does.</summary>
    public static bool RemoveDependency(string manifestPath, string name, IReadOnlyList<string> sections) {
        var original = File.ReadAllText(manifestPath);
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var removed = false;
        string? current = null;
        var skippingTable = false;
        var kept = new List<string>();
        foreach (var line in lines) {
            if (Header().Match(line) is { Success: true } match) {
                current = match.Groups[1].Value;
                skippingTable = sections.Any(s => current == $"{s}.{name}");
                removed |= skippingTable;
                if (skippingTable) {
                    continue;
                }
            } else if (AnyHeader().IsMatch(line)) {
                current = null;
                skippingTable = false;
            }

            if (skippingTable) {
                continue;
            }

            if (current is not null && sections.Contains(current) && IsKey(line, name)) {
                removed = true;
                continue;
            }

            kept.Add(line);
        }

        if (removed) {
            Save(manifestPath, original, string.Join('\n', kept));
        }

        return removed;
    }

    static void Save(string path, string original, string text) {
        File.WriteAllText(path, text);
        try {
            Manifest.Load(path);
        } catch (ManifestException) {
            File.WriteAllText(path, original);
            throw;
        }
    }
}
