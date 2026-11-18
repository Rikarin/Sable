using System.Text.RegularExpressions;

namespace Sable.Driver.Packages;

/// <summary>One thing wrong with a manifest: where, which key, and why.</summary>
public sealed record ManifestError(string File, int Line, string Key, string Message) {
    public override string ToString() => $"{File}:{Line}: {Key}: {Message}";
}

/// <summary>A manifest that cannot be used, with everything wrong with it.</summary>
public sealed class ManifestException(IReadOnlyList<ManifestError> errors)
    : Exception(string.Join(Environment.NewLine, errors.Select(static e => $"error: {e}"))) {
    public IReadOnlyList<ManifestError> Errors { get; } = errors;
}

/// <summary>A dependency as a manifest declares it.</summary>
public sealed record DependencySpec(
    string Name,
    VersionReq? Version,
    string? Path,
    string? Git,
    bool Optional,
    bool DefaultFeatures,
    IReadOnlyList<string> Features,
    int Line,
    string? When = null) {
    /// <summary>What a resolver holds it to: the version written, or any version of a path dependency.</summary>
    public VersionReq Requirement => Version ?? VersionReq.Any;

    /// <summary>
    ///     Whether it applies to a build for this machine: always, unless its
    ///     <c>when</c> names another operating system or architecture. The
    ///     resolver and the lock file keep it either way.
    /// </summary>
    public bool AppliesHere => When is null || DependencyCondition.Holds(When);
}

/// <summary>
///     A dependency's <c>when</c> (docs/05 §5.2.4): <c>os(macos|linux|windows)</c>
///     or <c>arch(arm64|x64)</c>, applied to the target being built — today
///     always this machine, since there is no cross-compilation.
/// </summary>
public static class DependencyCondition {
    public static bool IsValid(string text) => Parse(text) is not null;

    public static bool Holds(string text) =>
        Parse(text) switch {
            ("os", "macos") => OperatingSystem.IsMacOS(),
            ("os", "linux") => OperatingSystem.IsLinux(),
            ("os", "windows") => OperatingSystem.IsWindows(),
            ("arch", "arm64") => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64,
            ("arch", "x64") => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64,
            _ => false,
        };

    static (string Kind, string Value)? Parse(string text) {
        var open = text.IndexOf('(');
        if (open <= 0 || !text.EndsWith(')')) {
            return null;
        }

        var (kind, value) = (text[..open], text[(open + 1)..^1]);
        return (kind, value) switch {
            ("os", "macos" or "linux" or "windows") or ("arch", "arm64" or "x64") => (kind, value),
            _ => null,
        };
    }
}

public sealed record BinTarget(string Name, string Path);

/// <summary>
///     A build profile (<c>[profile.dev]</c>, <c>[profile.release]</c>). The
///     settings the compiler acts on are properties; the rest are accepted and
///     recorded in <see cref="Recorded" />, for the day it does.
/// </summary>
public sealed record Profile(
    string Name,
    int OptLevel,
    bool Debug,
    bool Exclusivity,
    bool OverflowChecks,
    bool ArcChecks,
    bool LeakCheck,
    IReadOnlyDictionary<string, string> Recorded) {
    public bool Optimise => OptLevel > 0;

    public static Profile Dev { get; } = new("dev", 0, true, true, true, true, false, new Dictionary<string, string>());

    public static Profile Release { get; } = new("release", 3, false, false, true, false, false, new Dictionary<string, string>());
}

public sealed record WorkspaceSpec(IReadOnlyList<string> Members, IReadOnlyList<string> Exclude);

/// <summary><c>[build]</c>: a build script and what it may touch (docs/05 §5.6) — read and checked, not yet run.</summary>
public sealed record BuildSpec(string Script, bool AllowNet, IReadOnlyList<string> AllowExec, IReadOnlyList<string> AllowRead, IReadOnlyList<string> AllowWrite);

/// <summary>
///     <c>sable.toml</c> (docs/05 §5.2), read and validated. Every error names
///     the file, the line and the key; a manifest with any error is refused
///     whole, with all of them.
/// </summary>
public sealed partial class Manifest {
    public required string File { get; init; }

    /// <summary>The <c>[package]</c> table; null for a virtual workspace root.</summary>
    public PackageInfo? Package { get; init; }

    /// <summary><c>[lib] path</c>, when given.</summary>
    public string? LibPath { get; init; }

    public IReadOnlyList<BinTarget> Bins { get; init; } = [];
    public IReadOnlyList<DependencySpec> Dependencies { get; init; } = [];
    public IReadOnlyList<DependencySpec> DevDependencies { get; init; } = [];
    public IReadOnlyList<DependencySpec> BuildDependencies { get; init; } = [];

    /// <summary>Each feature and what it turns on, as written.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Features { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public Profile Dev { get; init; } = Profile.Dev;
    public Profile Release { get; init; } = Profile.Release;
    public WorkspaceSpec? Workspace { get; init; }
    public BuildSpec? Build { get; init; }

    /// <summary><c>[registry] path</c>: where this package's registry is, relative to the manifest.</summary>
    public string? RegistryPath { get; init; }

    /// <summary>The <c>[lints]</c> table (docs/05 §5.7): each lint's level, which <c>sable check</c> reports at.</summary>
    public Sable.Symbols.LintConfiguration Lints { get; init; } = Sable.Symbols.LintConfiguration.Default;

    /// <summary>What was accepted but means nothing to this toolchain: unknown keys, settings not yet acted on.</summary>
    public IReadOnlyList<ManifestError> Warnings { get; init; } = [];

    public Profile ProfileFor(bool release) => release ? Release : Dev;

    /// <summary>
    ///     This manifest, read from the canonical TOML a <c>package.sb</c>
    ///     evaluated to, as the file it came from: its warnings name that file,
    ///     since the TOML's lines mean nothing to its author.
    /// </summary>
    internal Manifest Relocated(string file) => new() {
        File = file,
        Package = Package,
        LibPath = LibPath,
        Bins = Bins,
        Dependencies = Dependencies,
        DevDependencies = DevDependencies,
        BuildDependencies = BuildDependencies,
        Features = Features,
        Dev = Dev,
        Release = Release,
        Workspace = Workspace,
        Build = Build,
        RegistryPath = RegistryPath,
        Lints = Lints,
        Warnings = [..Warnings.Select(w => w with { File = file, Line = 1 })],
    };

    public sealed record PackageInfo(
        string Name,
        SemVersion Version,
        string Edition,
        IReadOnlyList<string> Authors,
        string? License,
        string? Description,
        string? Repository);

    [GeneratedRegex("^([a-z][a-z0-9_-]*/)?[a-z][a-z0-9_-]*$")]
    private static partial Regex PackageName();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_+.-]*$")]
    private static partial Regex FeatureName();

    /// <summary>Whether <paramref name="name" /> is a package name: lower-case, <c>org/name</c> allowed.</summary>
    public static bool IsValidPackageName(string name) => PackageName().IsMatch(name);

    /// <summary>Reads the manifest at <paramref name="path" />.</summary>
    /// <exception cref="ManifestException">It is not a valid manifest.</exception>
    public static Manifest Load(string path) => Parse(System.IO.File.ReadAllText(path), path);

    /// <exception cref="ManifestException">It is not a valid manifest.</exception>
    public static Manifest Parse(string text, string file = "sable.toml") {
        TomlTable root;
        try {
            root = TomlReader.Parse(text, file);
        } catch (TomlException e) {
            throw new ManifestException([new(file, e.Position.Line, "(syntax)", e.Reason)]);
        }

        return new Reader(file).Read(root);
    }

    /// <summary>Turns a TOML table into a manifest, collecting every error on the way.</summary>
    sealed class Reader(string file) {
        readonly List<ManifestError> _errors = [];
        readonly List<ManifestError> _warnings = [];

        void Error(TomlTable table, string key, string path, string message) => _errors.Add(new(file, table.KeyPosition(key).Line, path, message));

        void Error(TomlValue value, string path, string message) => _errors.Add(new(file, value.Position.Line, path, message));

        void Warn(TomlTable table, string key, string path, string message) => _warnings.Add(new(file, table.KeyPosition(key).Line, path, message));

        public Manifest Read(TomlTable root) {
            string[] known = ["package", "lib", "bin", "dependencies", "dev-dependencies", "build-dependencies", "features", "profile", "workspace", "reflect", "targets", "build", "registry", "lints"];
            foreach (var key in root.Keys.Where(k => !known.Contains(k))) {
                Warn(root, key, key, "unknown manifest key, ignored");
            }

            var package = Table(root, "package", "package") is { } p ? ReadPackage(p) : null;
            var workspace = Table(root, "workspace", "workspace") is { } w ? ReadWorkspace(w) : null;
            if (package is null && workspace is null && _errors.Count == 0) {
                _errors.Add(new(file, 1, "package", "a manifest needs a [package] table, or a [workspace] one"));
            }

            string? libPath = null;
            if (Table(root, "lib", "lib") is { } lib) {
                libPath = String(lib, "path", "lib.path");
                foreach (var key in lib.Keys.Where(static k => k != "path")) {
                    Warn(lib, key, $"lib.{key}", "unknown key, ignored");
                }
            }

            var bins = new List<BinTarget>();
            if (root["bin"] is { } binValue) {
                if (binValue is not TomlArray { IsArrayOfTables: true } array) {
                    Error(binValue, "bin", "must be an array of tables: [[bin]]");
                } else {
                    foreach (var element in array.Items.Cast<TomlTable>()) {
                        var name = String(element, "name", "bin.name");
                        var path = String(element, "path", "bin.path");
                        if (name is null) {
                            if (!element.TryGetValue("name", out _)) {
                                Error(element, "bin", "a [[bin]] needs a name");
                            }

                            continue;
                        }

                        if (bins.Any(b => b.Name == name)) {
                            Error(element, "name", "bin.name", $"two binaries are called '{name}'");
                        }

                        bins.Add(new(name, path ?? (bins.Count == 0 && package is not null && name == package.Name ? "src/main.sb" : $"src/bin/{name}.sb")));
                    }
                }
            }

            var dependencies = ReadDependencies(root, "dependencies");
            var devDependencies = ReadDependencies(root, "dev-dependencies");
            var buildDependencies = ReadDependencies(root, "build-dependencies");
            var features = ReadFeatures(root, dependencies);
            var (dev, release) = ReadProfiles(root);
            var build = Table(root, "build", "build") is { } b ? ReadBuild(b) : null;
            var registry = Table(root, "registry", "registry") is { } r ? String(r, "path", "registry.path") : null;
            var lints = Sable.Symbols.LintConfiguration.Default;
            if (Table(root, "lints", "lints") is { } lintTable) {
                var levels = new List<KeyValuePair<string, string>>();
                foreach (var key in lintTable.Keys) {
                    if (String(lintTable, key, $"lints.{key}") is { } level) {
                        levels.Add(new(key, level));
                    }
                }

                lints = Sable.Symbols.LintConfiguration.FromTable(levels, out var problems);
                foreach (var problem in problems) {
                    _errors.Add(new(file, lintTable.Position.Line, "lints", problem));
                }
            }

            if (_errors.Count > 0) {
                throw new ManifestException(_errors);
            }

            return new() {
                File = file,
                Package = package,
                LibPath = libPath,
                Bins = bins,
                Dependencies = dependencies,
                DevDependencies = devDependencies,
                BuildDependencies = buildDependencies,
                Features = features,
                Dev = dev,
                Release = release,
                Workspace = workspace,
                Build = build,
                RegistryPath = registry,
                Lints = lints,
                Warnings = _warnings,
            };
        }

        TomlTable? Table(TomlTable parent, string key, string path) {
            if (parent[key] is not { } value) {
                return null;
            }

            if (value is TomlTable table) {
                return table;
            }

            Error(parent, key, path, $"must be a table, not {value.Kind}");
            return null;
        }

        string? String(TomlTable table, string key, string path) {
            if (table[key] is not { } value) {
                return null;
            }

            if (value is TomlString s) {
                return s.Value;
            }

            Error(table, key, path, $"must be a string, not {value.Kind}");
            return null;
        }

        bool? Bool(TomlTable table, string key, string path) {
            if (table[key] is not { } value) {
                return null;
            }

            if (value is TomlBoolean b) {
                return b.Value;
            }

            Error(table, key, path, $"must be true or false, not {value.Kind}");
            return null;
        }

        List<string>? Strings(TomlTable table, string key, string path) {
            if (table[key] is not { } value) {
                return null;
            }

            if (value is TomlArray array && array.Items.All(static i => i is TomlString)) {
                return [..array.Items.Cast<TomlString>().Select(static s => s.Value)];
            }

            Error(table, key, path, "must be an array of strings");
            return null;
        }

        PackageInfo? ReadPackage(TomlTable table) {
            string[] known = ["name", "version", "edition", "authors", "license", "description", "repository", "readme", "keywords", "categories", "homepage", "documentation", "include", "exclude", "publish"];
            foreach (var key in table.Keys.Where(k => !known.Contains(k))) {
                Warn(table, key, $"package.{key}", "unknown key, ignored");
            }

            var name = String(table, "name", "package.name");
            if (name is null && table["name"] is null) {
                Error(table, "package", "package.name", "a package needs a name");
            } else if (name is not null && !IsValidPackageName(name)) {
                Error(table, "name", "package.name", $"'{name}' is not a package name: lower-case letters, digits, '-' and '_', starting with a letter, optionally 'org/name'");
            }

            var versionText = String(table, "version", "package.version");
            SemVersion? version = null;
            if (versionText is null && table["version"] is null) {
                Error(table, "package", "package.version", "a package needs a version");
            } else if (versionText is not null) {
                if (SemVersion.TryParse(versionText, out var v, out var error)) {
                    version = v;
                } else {
                    Error(table, "version", "package.version", $"'{versionText}' is not a semantic version: {error}");
                }
            }

            var edition = String(table, "edition", "package.edition") ?? DefaultEdition;
            if (edition.Length != 4 || !edition.All(char.IsAsciiDigit)) {
                Error(table, "edition", "package.edition", $"'{edition}' is not an edition: an edition is a year, like \"{DefaultEdition}\"");
            }

            var authors = Strings(table, "authors", "package.authors") ?? [];
            var license = String(table, "license", "package.license");
            var description = String(table, "description", "package.description");
            var repository = String(table, "repository", "package.repository");
            return name is null || version is null ? null : new(name, version, edition, authors, license, description, repository);
        }

        WorkspaceSpec ReadWorkspace(TomlTable table) {
            foreach (var key in table.Keys.Where(static k => k is not ("members" or "exclude" or "resolver"))) {
                Warn(table, key, $"workspace.{key}", "unknown key, ignored");
            }

            return new(Strings(table, "members", "workspace.members") ?? [], Strings(table, "exclude", "workspace.exclude") ?? []);
        }

        List<DependencySpec> ReadDependencies(TomlTable root, string section) {
            var result = new List<DependencySpec>();
            if (Table(root, section, section) is not { } table) {
                return result;
            }

            foreach (var (name, value) in table.Entries) {
                var path = $"{section}.{name}";
                var line = table.KeyPosition(name).Line;
                if (!IsValidPackageName(name)) {
                    Error(table, name, path, $"'{name}' is not a package name");
                    continue;
                }

                switch (value) {
                    case TomlString s:
                        if (Requirement(s.Value, value, path) is { } requirement) {
                            result.Add(new(name, requirement, null, null, false, true, [], line));
                        }

                        break;
                    case TomlTable detail:
                        string[] known = ["version", "path", "git", "tag", "branch", "rev", "optional", "default-features", "features", "package", "registry", "when"];
                        foreach (var key in detail.Keys.Where(k => !known.Contains(k))) {
                            Error(detail, key, $"{path}.{key}", "unknown key in a dependency");
                        }

                        var versionText = String(detail, "version", $"{path}.version");
                        var version = versionText is null ? null : Requirement(versionText, detail[$"version"]!, $"{path}.version");
                        var dependencyPath = String(detail, "path", $"{path}.path");
                        var git = String(detail, "git", $"{path}.git");
                        if (versionText is null && dependencyPath is null && git is null && !detail.Keys.Any(static k => k is "version" or "path" or "git")) {
                            Error(table, name, path, "a dependency needs a version, a path or a git repository");
                        }

                        if (git is not null) {
                            Error(detail, "git", $"{path}.git", "git dependencies are not supported yet: use a path or the registry");
                        }

                        var when = String(detail, "when", $"{path}.when");
                        if (when is not null && !DependencyCondition.IsValid(when)) {
                            Error(detail, "when", $"{path}.when", $"'{when}' is not a condition: os(macos), os(linux), os(windows), arch(arm64) or arch(x64)");
                        }

                        result.Add(new(
                            name,
                            version,
                            dependencyPath,
                            git,
                            Bool(detail, "optional", $"{path}.optional") ?? false,
                            Bool(detail, "default-features", $"{path}.default-features") ?? true,
                            Strings(detail, "features", $"{path}.features") ?? [],
                            line,
                            when));
                        break;
                    default:
                        Error(table, name, path, $"must be a version requirement or a table, not {value.Kind}");
                        break;
                }
            }

            return result;
        }

        VersionReq? Requirement(string text, TomlValue value, string path) {
            if (VersionReq.TryParse(text, out var requirement, out var error)) {
                return requirement;
            }

            Error(value, path, error);
            return null;
        }

        Dictionary<string, IReadOnlyList<string>> ReadFeatures(TomlTable root, List<DependencySpec> dependencies) {
            var features = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (Table(root, "features", "features") is not { } table) {
                return features;
            }

            foreach (var (name, _) in table.Entries) {
                var path = $"features.{name}";
                if (!FeatureName().IsMatch(name)) {
                    Error(table, name, path, $"'{name}' is not a feature name");
                    continue;
                }

                if (Strings(table, name, path) is { } values) {
                    features[name] = values;
                }
            }

            // What each feature turns on must exist.
            var optional = dependencies.Where(static d => d.Optional).Select(static d => d.Name).ToHashSet(StringComparer.Ordinal);
            var all = dependencies.Select(static d => d.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var (name, values) in features) {
                foreach (var value in values) {
                    var path = $"features.{name}";
                    if (value.StartsWith("dep:", StringComparison.Ordinal)) {
                        if (!optional.Contains(value[4..])) {
                            Error(table, name, path, all.Contains(value[4..])
                                ? $"'{value}' names a dependency that is not optional: mark it 'optional = true'"
                                : $"'{value}' names no dependency");
                        }
                    } else if (value.IndexOf('/', StringComparison.Ordinal) is var slash and > 0) {
                        var dependency = value[..slash].TrimEnd('?');
                        if (!all.Contains(dependency)) {
                            Error(table, name, path, $"'{value}' names no dependency '{dependency}'");
                        }
                    } else if (!features.ContainsKey(value) && !optional.Contains(value)) {
                        Error(table, name, path, $"'{value}' is neither a feature nor an optional dependency");
                    } else if (value == name) {
                        Error(table, name, path, $"the feature '{name}' turns on itself");
                    }
                }
            }

            return features;
        }

        (Profile Dev, Profile Release) ReadProfiles(TomlTable root) {
            var dev = Profile.Dev;
            var release = Profile.Release;
            if (Table(root, "profile", "profile") is not { } profiles) {
                return (dev, release);
            }

            foreach (var (name, value) in profiles.Entries) {
                if (name is not ("dev" or "release")) {
                    Error(profiles, name, $"profile.{name}", "there are two profiles, 'dev' and 'release'");
                    continue;
                }

                if (value is not TomlTable table) {
                    Error(profiles, name, $"profile.{name}", $"must be a table, not {value.Kind}");
                    continue;
                }

                var profile = ReadProfile(table, name == "dev" ? dev : release);
                if (name == "dev") {
                    dev = profile;
                } else {
                    release = profile;
                }
            }

            return (dev, release);
        }

        Profile ReadProfile(TomlTable table, Profile defaults) {
            var profile = defaults;
            var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in table.Entries) {
                var path = $"profile.{defaults.Name}.{key}";
                switch (key) {
                    case "opt-level":
                        profile = value switch {
                            TomlInteger { Value: >= 0 and <= 3 } level => profile with { OptLevel = (int)level.Value },
                            TomlString { Value: "s" or "z" } => profile with { OptLevel = 2 },
                            _ => Invalid(profile, value, path, "must be 0, 1, 2, 3, \"s\" or \"z\""),
                        };
                        break;
                    case "debug":
                        profile = value switch {
                            TomlBoolean b => profile with { Debug = b.Value },
                            TomlInteger { Value: >= 0 and <= 2 } i => profile with { Debug = i.Value > 0 },
                            TomlString { Value: "none" or "line-tables-only" or "limited" or "full" } s => profile with { Debug = s.Value != "none" },
                            _ => Invalid(profile, value, path, "must be true, false, 0, 1 or 2"),
                        };
                        break;
                    case "exclusivity":
                        profile = value switch {
                            TomlString { Value: "checked" } => profile with { Exclusivity = true },
                            TomlString { Value: "unchecked" } => profile with { Exclusivity = false },
                            _ => Invalid(profile, value, path, "must be \"checked\" or \"unchecked\""),
                        };
                        break;
                    case "overflow-checks" or "arc-checks" or "leak-check" or "debug-assertions" or "incremental":
                        if (value is not TomlBoolean flag) {
                            Error(value, path, $"must be true or false, not {value.Kind}");
                            break;
                        }

                        profile = key switch {
                            "overflow-checks" => profile with { OverflowChecks = flag.Value },
                            "arc-checks" => profile with { ArcChecks = flag.Value },
                            "leak-check" => profile with { LeakCheck = flag.Value },
                            _ => profile,
                        };
                        recorded[key] = flag.Value ? "true" : "false";
                        break;
                    case "lto" or "codegen-units" or "panic" or "strip" or "rpath":
                        recorded[key] = value switch {
                            TomlString s => s.Value,
                            TomlBoolean b => b.Value ? "true" : "false",
                            TomlInteger i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            _ => value.Kind,
                        };
                        break;
                    default:
                        Warn(table, key, path, "unknown profile setting, ignored");
                        break;
                }
            }

            // Arithmetic is always checked, so overflow-checks = false is recorded but has no effect yet.
            if (!profile.OverflowChecks) {
                Warn(table, "overflow-checks", $"profile.{defaults.Name}.overflow-checks", "integer overflow is always checked; 'false' has no effect yet");
            }

            return profile with { Recorded = recorded };
        }

        Profile Invalid(Profile profile, TomlValue value, string path, string message) {
            Error(value, path, message);
            return profile;
        }

        BuildSpec? ReadBuild(TomlTable table) {
            var script = String(table, "script", "build.script");
            if (script is null) {
                if (table["script"] is null) {
                    Error(table, "build", "build.script", "[build] needs a script");
                }

                return null;
            }

            return new(
                script,
                Bool(table, "allow-net", "build.allow-net") ?? false,
                Strings(table, "allow-exec", "build.allow-exec") ?? [],
                Strings(table, "allow-read", "build.allow-read") ?? [],
                Strings(table, "allow-write", "build.allow-write") ?? []);
        }
    }

    /// <summary>The edition a new package is written in.</summary>
    public const string DefaultEdition = "2027";
}
