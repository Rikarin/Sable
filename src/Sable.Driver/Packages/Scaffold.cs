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
        environment.Status.WriteLine($"     Created {(library ? "library" : "binary")} package `{name}` in {directory}");
        JoinWorkspace(directory, environment);
        return directory;
    }

    /// <summary><c>sable init</c>: a package in an existing directory, named for it unless a name is given.</summary>
    public static void Init(string directory, string? name, bool library, PackageEnvironment environment) {
        directory = Path.GetFullPath(directory);
        if (ManifestFiles.Exists(directory)) {
            throw new PackageException($"'{directory}' already has a manifest ({Path.GetFileName(ManifestFiles.PathIn(directory))})");
        }

        name ??= NameFrom(Path.GetFileName(directory));
        Write(directory, name, library);
        environment.Status.WriteLine($"     Created {(library ? "library" : "binary")} package `{name}` in {directory}");
        JoinWorkspace(directory, environment);
    }

    /// <summary>
    ///     A package made inside a workspace becomes one of its members, as
    ///     Cargo does it: otherwise the next command run in it finds the
    ///     workspace above, which does not list it, and builds as though it
    ///     were not there. One the workspace excludes is left out; one whose
    ///     <c>members</c> cannot be edited safely gets a warning saying what
    ///     to add.
    /// </summary>
    static void JoinWorkspace(string directory, PackageEnvironment environment) {
        for (var parent = Directory.GetParent(directory); parent is not null; parent = parent.Parent) {
            if (!ManifestFiles.Exists(parent.FullName)) {
                continue;
            }

            Manifest manifest;
            try {
                manifest = ManifestFiles.Load(parent.FullName);
            } catch (ManifestException) {
                // A broken manifest above is not this package's to report.
                return;
            }

            if (manifest.Workspace is not { } workspace) {
                // A package above that is not a workspace: look further up.
                continue;
            }

            if (Workspace.MemberDirectories(parent.FullName, workspace).Contains(directory) || Workspace.IsExcluded(parent.FullName, workspace, directory)) {
                return;
            }

            var member = Path.GetRelativePath(parent.FullName, directory).Replace(Path.DirectorySeparatorChar, '/');
            var file = ManifestFiles.PathIn(parent.FullName);
            try {
                var original = File.ReadAllText(file);
                var edited = Path.GetFileName(file) == ManifestFiles.TomlFile
                    ? ManifestEditor.AddWorkspaceMember(original, file, member)
                    : SableManifestEditor.AddWorkspaceMember(original, file, member);
                File.WriteAllText(file, edited);
                try {
                    ManifestFiles.Load(parent.FullName);
                } catch (ManifestException) {
                    File.WriteAllText(file, original);
                    throw new PackageException("the edited manifest was not valid, so it was put back");
                }

                environment.Status.WriteLine($"      Adding `{member}` as a member of the workspace at {parent.FullName}");
            } catch (PackageException e) {
                environment.Error.WriteLine($"warning: this package is inside the workspace at {parent.FullName}, which does not list it: add \"{member}\" to its members ({e.Message})");
            }

            return;
        }
    }

    static void Write(string directory, string name, bool library) {
        if (!Manifest.IsValidPackageName(name)) {
            throw new PackageException($"'{name}' is not a package name: lower-case letters, digits, '-' and '_', starting with a letter, optionally 'org/name'");
        }

        var source = Path.Combine(directory, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(directory, ManifestFiles.PackageFile), $$"""
            import Sable.Build

            let manifest = Package {
                name: "{{name}}",
                version: "0.1.0",
                edition: {{Manifest.DefaultEdition}},
                dependencies: [],
            }

            """);

        var module = ModuleName(name);
        var file = Path.Combine(source, library ? "lib.sb" : "main.sb");
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

                    fn main() {
                        print("Hello, world!")
                    }

                    """);
        }

        var ignore = Path.Combine(directory, ".gitignore");
        if (!File.Exists(ignore)) {
            // A library does not commit its lock file (docs/05 §5.3): the
            // versions it is built with are chosen by whatever depends on it.
            File.WriteAllText(ignore, library ? $"/target\n/{Workspace.LockName}\n" : "/target\n");
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

    /// <summary>
    ///     Adds <paramref name="member" /> to <c>[workspace] members</c>,
    ///     before the list's closing bracket, laid out as the list is.
    /// </summary>
    /// <exception cref="PackageException">There is no list written there to add to.</exception>
    public static string AddWorkspaceMember(string text, string file, string member) {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var header = lines.FindIndex(l => Header().Match(l) is { Success: true } m && m.Groups[1].Value == "workspace");
        if (header < 0) {
            throw new PackageException($"{file} has no [workspace] table");
        }

        var end = lines.FindIndex(header + 1, l => AnyHeader().IsMatch(l));
        end = end < 0 ? lines.Count : end;
        var entry = TomlText.Quote(member);
        var key = lines.FindIndex(header + 1, end - header - 1, l => IsKey(l, "members"));
        if (key < 0) {
            lines.Insert(header + 1, $"members = [{entry}]");
            return string.Join('\n', lines);
        }

        // The list's closing bracket: the first ']' after its '[' that is
        // not in a string or a comment.
        var joined = string.Join('\n', lines);
        var start = lines.Take(key).Sum(static l => l.Length + 1);
        var open = joined.IndexOf('[', joined.IndexOf('=', start));
        var close = -1;
        var quote = '\0';
        for (var i = open + 1; open > 0 && i < joined.Length && close < 0; i++) {
            var c = joined[i];
            if (quote != '\0') {
                if (c == '\\' && quote == '"') {
                    i++;
                } else if (c == quote) {
                    quote = '\0';
                }
            } else if (c is '"' or '\'') {
                quote = c;
            } else if (c == '#') {
                i = joined.IndexOf('\n', i) is var newline and >= 0 ? newline : joined.Length;
            } else if (c == ']') {
                close = i;
            } else if (c == '[') {
                break;
            }
        }

        if (open < 0 || close < 0) {
            throw new PackageException($"{file}: [workspace] members is not written as a list");
        }

        var inside = joined[(open + 1)..close];
        var body = inside.TrimEnd();
        var trailingComma = body.EndsWith(',');
        string replacement;
        if (body.Trim().Length == 0) {
            replacement = entry;
        } else if (inside.Contains('\n', StringComparison.Ordinal)) {
            // One to a line: a new line, indented as the last one.
            var lastLine = body[(body.LastIndexOf('\n') + 1)..];
            var indent = lastLine[..(lastLine.Length - lastLine.TrimStart().Length)];
            replacement = body + (trailingComma ? "" : ",") + $"\n{indent}{entry}" + (trailingComma ? "," : "") + inside[body.Length..];
        } else {
            replacement = body.TrimEnd(',') + $", {entry}" + (trailingComma ? "," : "") + inside[body.Length..];
        }

        return joined[..(open + 1)] + replacement + joined[close..];
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
