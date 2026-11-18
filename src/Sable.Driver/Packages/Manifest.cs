using System.Text.RegularExpressions;
using Sable.CodeGen;

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
    string? When = null,
    GitReference? Reference = null) {
    /// <summary>What a resolver holds it to: the version written, or any version of a path dependency.</summary>
    public VersionReq Requirement => Version ?? VersionReq.Any;

    /// <summary>
    ///     Whether it applies to a build for this machine: always, unless its
    ///     <c>when</c> names another operating system or architecture. The
    ///     resolver and the lock file keep it either way.
    /// </summary>
    public bool AppliesHere => AppliesTo(Sable.Syntax.ParseOptions.HostOs, Sable.Syntax.ParseOptions.HostArch);

    /// <summary>Whether it applies to a build for the target, its operating system and architecture as <c>#if</c> names them.</summary>
    public bool AppliesTo(string os, string arch) => When is null || DependencyCondition.Holds(When, os, arch);
}

/// <summary>
///     Which commit of a git dependency to use: a <c>tag</c>, a
///     <c>branch</c>'s tip, or a <c>rev</c> (a commit, or anything
///     <c>git rev-parse</c> takes); with none, the repository's default branch.
/// </summary>
public sealed record GitReference(string Kind, string Value) {
    /// <summary>As the lock file writes it after a git URL: <c>tag=v1.0</c>.</summary>
    public override string ToString() => $"{Kind}={Value}";
}

/// <summary>
///     A dependency's <c>when</c> (docs/05 §5.2.4): <c>os(macos|linux|windows)</c>
///     or <c>arch(arm64|x64)</c>, applied to the target being built: this
///     machine, or the one <c>--target</c> names.
/// </summary>
public static class DependencyCondition {
    public static bool IsValid(string text) => Parse(text) is not null;

    /// <summary>Whether the condition holds on this machine.</summary>
    public static bool Holds(string text) => Holds(text, Sable.Syntax.ParseOptions.HostOs, Sable.Syntax.ParseOptions.HostArch);

    /// <summary>
    ///     Whether the condition holds for a target, its operating system and
    ///     architecture as <c>#if</c> names them (<c>macos</c>, <c>x86_64</c>):
    ///     the manifest's <c>x64</c> is <c>#if</c>'s <c>x86_64</c>.
    /// </summary>
    public static bool Holds(string text, string os, string arch) =>
        Parse(text) switch {
            ("os", var name) => name == os,
            ("arch", "x64") => arch == "x86_64",
            ("arch", var name) => name == arch,
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
///     settings the compiler acts on are properties; the flags and the settings
///     it cannot act on yet are also recorded in <see cref="Recorded" />, as written.
/// </summary>
/// <param name="OptLevel">clang's level: <c>0</c> to <c>3</c>, <c>s</c> or <c>z</c>.</param>
public sealed record Profile(
    string Name,
    string OptLevel,
    bool Debug,
    bool Exclusivity,
    bool OverflowChecks,
    bool ArcChecks,
    bool LeakCheck,
    IReadOnlyDictionary<string, string> Recorded) {
    public bool Optimise => OptLevel != "0";

    /// <summary>Link-time optimisation, in an optimised build.</summary>
    public LtoMode Lto { get; init; } = LtoMode.Fat;

    /// <summary>
    ///     <c>codegen-units</c>: 1 compiles the program whole even in an
    ///     unoptimised debug build, which otherwise compiles each package apart.
    /// </summary>
    public int? CodegenUnits { get; init; }

    public StripMode Strip { get; init; } = StripMode.None;
    public bool Rpath { get; init; }

    /// <summary>What clang is asked for.</summary>
    public NativeOptions Native => new() { OptLevel = OptLevel, DebugInfo = Debug, Lto = Lto, Strip = Strip, Rpath = Rpath };

    public static Profile Dev { get; } = new("dev", "0", true, true, true, true, false, new Dictionary<string, string>());

    public static Profile Release { get; } = new("release", "3", false, false, true, false, false, new Dictionary<string, string>());
}

public sealed record WorkspaceSpec(IReadOnlyList<string> Members, IReadOnlyList<string> Exclude);

/// <summary><c>[build]</c>: a build script and what it may touch (docs/05 §5.6), run by <see cref="BuildScripts" />.</summary>
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

    /// <summary>
    ///     <c>[generators] inputs</c> (docs/08 §8.3): the files and directories,
    ///     relative to the package, whose files its package generators are given.
    /// </summary>
    public IReadOnlyList<string> GeneratorInputs { get; init; } = [];

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
        GeneratorInputs = GeneratorInputs,
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
            string[] known = ["package", "lib", "bin", "dependencies", "dev-dependencies", "build-dependencies", "features", "profile", "workspace", "reflect", "targets", "build", "registry", "lints", "generators"];
            foreach (var key in root.Keys.Where(k => !known.Contains(k))) {
                if (key == "target") {
                    // Cargo's [target.'cfg(...)'.dependencies]: ignoring it would build without them.
                    Error(root, key, key, "there are no platform-specific dependency tables: give the dependency `when = \"os(linux)\"` (or `when: .os(.linux)` in package.sb)");
                } else {
                    Warn(root, key, key, "unknown manifest key, ignored");
                }
            }

            // Accepted, so a manifest written for a later toolchain still
            // reads, but nothing here acts on them yet: say so.
            if (root["reflect"] is not null) {
                Warn(root, "reflect", "reflect",
                    "has no effect yet: reflection is generated where a program asks for it, and nothing looks a type up by name (docs/04 §4.2)");
            }

            if (root["targets"] is not null) {
                Warn(root, "targets", "targets", "per-target settings (a runtime profile, reflection) are not built yet (docs/05 §5.2.10); ignored. To build for another machine, pass --target");
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
            if (buildDependencies.Count > 0 && root["build"] is null) {
                Warn(root, "build-dependencies", "build-dependencies",
                    "are for a build script, and there is no [build] script to use them (docs/05 §5.6)");
            }
            var features = ReadFeatures(root, dependencies);
            var (dev, release) = ReadProfiles(root);
            var build = Table(root, "build", "build") is { } b ? ReadBuild(b) : null;
            var registry = Table(root, "registry", "registry") is { } r ? String(r, "path", "registry.path") : null;
            var lints = Sable.Symbols.LintConfiguration.Default;
            if (Table(root, "lints", "lints") is { } lintTable) {
                // 'plugins' names assemblies of more lints (docs/05 §5.7),
                // relative to the manifest: loaded first, so the levels
                // below may name their lints too.
                foreach (var plugin in Strings(lintTable, "plugins", "lints.plugins") ?? []) {
                    var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".", plugin);
                    try {
                        lints = lints.WithPlugin(Sable.Symbols.Lints.LoadPlugin(path));
                    } catch (Exception e) when (e is Sable.Symbols.LintPluginException or ArgumentException) {
                        Error(lintTable, "plugins", "lints.plugins", e.Message);
                    }
                }

                var levels = new List<KeyValuePair<string, string>>();
                foreach (var key in lintTable.Keys.Where(static k => k != "plugins")) {
                    if (String(lintTable, key, $"lints.{key}") is { } level) {
                        levels.Add(new(key, level));
                    }
                }

                lints = Sable.Symbols.LintConfiguration.FromTable(levels, out var problems, lints);
                foreach (var problem in problems) {
                    _errors.Add(new(file, lintTable.Position.Line, "lints", problem));
                }
            }

            var generatorInputs = Table(root, "generators", "generators") is { } g ? Strings(g, "inputs", "generators.inputs") ?? [] : [];
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
                GeneratorInputs = generatorInputs,
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

            foreach (var (key, value) in table.Entries) {
                if (value is TomlTable { } inherited && inherited["workspace"] is not null) {
                    Error(table, key, $"package.{key}", "inheriting a setting from the workspace is not supported yet (docs/05 §5.2.10): write it out here");
                }
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
                Warn(table, key, $"workspace.{key}",
                    key is "package" or "dependencies" or "metadata"
                        ? "members cannot inherit from the workspace yet (docs/05 §5.2.10); ignored"
                        : "unknown key, ignored");
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
                        string[] known = ["version", "path", "git", "tag", "branch", "rev", "optional", "default-features", "features", "when"];
                        foreach (var key in detail.Keys.Where(k => !known.Contains(k))) {
                            Error(detail, key, $"{path}.{key}", key switch {
                                // Code imports a package's modules and never names the
                                // package, so a second name for it would mean nothing.
                                "package" => "a dependency cannot be renamed: code imports its modules, not its name, so the key must be the package's name",
                                "registry" => "every dependency comes from the workspace's registry ([registry] path, else $SABLE_REGISTRY); naming another one is not supported",
                                "workspace" => "inheriting a dependency from the workspace is not supported yet (docs/05 §5.2.10): write it out here",
                                _ => "unknown key in a dependency",
                            });
                        }

                        var versionText = String(detail, "version", $"{path}.version");
                        var version = versionText is null ? null : Requirement(versionText, detail[$"version"]!, $"{path}.version");
                        var dependencyPath = String(detail, "path", $"{path}.path");
                        var git = String(detail, "git", $"{path}.git");
                        if (versionText is null && dependencyPath is null && git is null && !detail.Keys.Any(static k => k is "version" or "path" or "git")) {
                            Error(table, name, path, "a dependency needs a version, a path or a git repository");
                        }

                        GitReference? reference = null;
                        foreach (var kind in new[] { "tag", "branch", "rev" }) {
                            if (String(detail, kind, $"{path}.{kind}") is not { } referenced) {
                                continue;
                            }

                            if (git is null) {
                                Error(detail, kind, $"{path}.{kind}", $"'{kind}' says which commit of a git dependency to use, and this one has no 'git'");
                            } else if (reference is not null) {
                                Error(detail, kind, $"{path}.{kind}", $"a git dependency takes one of 'tag', 'branch' and 'rev', and this one already has '{reference.Kind}'");
                            } else if (referenced.Length == 0) {
                                Error(detail, kind, $"{path}.{kind}", $"'{kind}' cannot be empty");
                            } else {
                                reference = new(kind, referenced);
                            }
                        }

                        if (git is not null && dependencyPath is not null) {
                            Error(detail, "git", $"{path}.git", "a dependency comes from a path or from git, not both");
                        } else if (git is not null && !GitSource.IsUrl(git)) {
                            Error(detail, "git", $"{path}.git", $"'{git}' is not a git repository: a URL (https://, ssh://, git://, file://) or user@host:path");
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
                            when,
                            reference));
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
                            TomlInteger { Value: >= 0 and <= 3 } level => profile with { OptLevel = Written(level) },
                            TomlString { Value: "s" or "z" } size => profile with { OptLevel = size.Value },
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
                    case "overflow-checks" or "arc-checks" or "leak-check" or "debug-assertions" or "incremental" or "rpath":
                        if (value is not TomlBoolean flag) {
                            Error(value, path, $"must be true or false, not {value.Kind}");
                            break;
                        }

                        profile = key switch {
                            "overflow-checks" => profile with { OverflowChecks = flag.Value },
                            "arc-checks" => profile with { ArcChecks = flag.Value },
                            "leak-check" => profile with { LeakCheck = flag.Value },
                            "rpath" => profile with { Rpath = flag.Value },
                            _ => profile,
                        };
                        recorded[key] = flag.Value ? "true" : "false";

                        // The runtime has no reference-count checks beyond the
                        // ones every build makes, so there is nothing to turn
                        // on or off: say so rather than take it silently.
                        if (key == "arc-checks") {
                            Warn(table, key, path, "there are no optional ARC checks yet; this has no effect");
                        }

                        break;
                    case "lto":
                        profile = value switch {
                            TomlBoolean { Value: false } or TomlString { Value: "off" } => profile with { Lto = LtoMode.Off },
                            TomlBoolean { Value: true } or TomlString { Value: "fat" } => profile with { Lto = LtoMode.Fat },
                            TomlString { Value: "thin" } => profile with { Lto = LtoMode.Thin },
                            _ => Invalid(profile, value, path, "must be true, false, \"fat\", \"thin\" or \"off\""),
                        };
                        recorded[key] = Written(value);
                        break;
                    case "codegen-units":
                        profile = value switch {
                            TomlInteger { Value: >= 1 and <= 256 } units => profile with { CodegenUnits = (int)units.Value },
                            _ => Invalid(profile, value, path, "must be a whole number from 1 to 256"),
                        };
                        recorded[key] = Written(value);
                        break;
                    case "panic":
                        // A trap ends the program, as 'abort' says: there is no
                        // unwinding to ask for.
                        if (value is TomlString { Value: "unwind" }) {
                            Warn(table, key, path, "a trap always aborts the program; 'unwind' has no effect");
                        } else if (value is not TomlString { Value: "abort" }) {
                            Error(value, path, "must be \"abort\" or \"unwind\"");
                        }

                        recorded[key] = Written(value);
                        break;
                    case "strip":
                        profile = value switch {
                            TomlBoolean { Value: false } or TomlString { Value: "none" } => profile with { Strip = StripMode.None },
                            TomlBoolean { Value: true } or TomlString { Value: "symbols" } => profile with { Strip = StripMode.Symbols },
                            TomlString { Value: "debuginfo" } => profile with { Strip = StripMode.DebugInfo },
                            _ => Invalid(profile, value, path, "must be true, false, \"none\", \"debuginfo\" or \"symbols\""),
                        };
                        recorded[key] = Written(value);
                        break;
                    default:
                        Warn(table, key, path, "unknown profile setting, ignored");
                        break;
                }
            }

            return profile with { Recorded = recorded };
        }

        static string Written(TomlValue value) =>
            value switch {
                TomlString s => s.Value,
                TomlBoolean b => b.Value ? "true" : "false",
                TomlInteger i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => value.Kind,
            };

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
