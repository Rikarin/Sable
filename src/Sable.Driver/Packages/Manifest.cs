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
///     A build profile (<c>[profile.dev]</c>, <c>[profile.release]</c>, or
///     one of the package's own that inherits one of them). The
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

    /// <summary>
    ///     Link-time optimisation, in an optimised build: by default ThinLTO,
    ///     which compiles each package apart, cached, and inlines across them
    ///     as they link; <c>"fat"</c> compiles the program whole, so that the
    ///     SIL passes see all of it.
    /// </summary>
    public LtoMode Lto { get; init; } = LtoMode.Thin;

    /// <summary>
    ///     <c>codegen-units</c>: 1 compiles the program whole even in an
    ///     unoptimised debug build, which otherwise compiles each package apart.
    /// </summary>
    public int? CodegenUnits { get; init; }

    public StripMode Strip { get; init; } = StripMode.None;
    public bool Rpath { get; init; }

    /// <summary>
    ///     <c>incremental</c>: false compiles the program whole every time
    ///     it changes, keeping no unit's object between builds, as
    ///     <c>codegen-units = 1</c> does. An unchanged build is still skipped.
    /// </summary>
    public bool Incremental { get; init; } = true;

    /// <summary><c>debug-assertions</c> as written; null when the profile leaves it to the opt-level.</summary>
    public bool? DebugAssertionsSetting { get; init; }

    /// <summary>
    ///     Whether the build defines <c>DEBUG</c> rather than <c>RELEASE</c>
    ///     (SYNTAX.md §21), in the program's files and the library's: as
    ///     <c>debug-assertions</c> says, or else in an unoptimised build.
    /// </summary>
    public bool DebugAssertions => DebugAssertionsSetting ?? !Optimise;

    /// <summary>What clang is asked for.</summary>
    public NativeOptions Native => new() { OptLevel = OptLevel, DebugInfo = Debug, Lto = Lto, Strip = Strip, Rpath = Rpath };

    /// <summary>
    ///     What the profile asks for that a build for <paramref name="target" />
    ///     cannot do, each a warning: said rather than taken silently. Only
    ///     what the manifest wrote is warned about, not a default.
    /// </summary>
    public IEnumerable<string> UnmetFor(NativeTarget target) {
        var platform = target.Platform;
        if (Optimise && Lto != LtoMode.Off && Recorded.ContainsKey("lto") && !NativeBuild.LinksBitcodeFor(target)) {
            yield return $"profile.{Name}.lto: the link for {target.Triple} cannot take LLVM bitcode (it needs lld, which clang cannot use here); built without link-time optimisation";
        }

        if (Strip != StripMode.None && platform is not (NativePlatform.MacOS or NativePlatform.Linux)) {
            yield return $"profile.{Name}.strip: has no effect for {target.Triple}: only macOS and Linux links strip";
        }

        if (Rpath && platform is not (NativePlatform.MacOS or NativePlatform.Linux)) {
            yield return $"profile.{Name}.rpath: has no effect for {target.Triple}, which looks for a shared library beside the executable already";
        }
    }

    public static Profile Dev { get; } = new("dev", "0", true, true, true, true, false, new Dictionary<string, string>());

    public static Profile Release { get; } = new("release", "3", false, false, true, false, false, new Dictionary<string, string>());
}

public sealed record WorkspaceSpec(IReadOnlyList<string> Members, IReadOnlyList<string> Exclude) {
    /// <summary><c>[workspace.package]</c>: settings a member's <c>[package]</c> takes with <c>key.workspace = true</c>.</summary>
    public TomlTable? Package { get; init; }

    /// <summary>
    ///     <c>[workspace.dependencies]</c>: dependencies a member takes with
    ///     <c>name = { workspace = true }</c>; a path in one is relative to the
    ///     workspace's directory.
    /// </summary>
    public IReadOnlyList<DependencySpec> Dependencies { get; init; } = [];
}

/// <summary>
///     What a member's manifest inherits from: its workspace's settings, and
///     the directory a workspace dependency's path is relative to.
/// </summary>
public sealed record WorkspaceInheritance(string Directory, string File, WorkspaceSpec Workspace);

/// <summary>
///     <c>[targets.&lt;triple&gt;]</c> (docs/05 §5.2.10): what a build for that
///     target is, beyond what <c>--target</c> says. The triple is matched by
///     its operating system and architecture, as <c>--target</c> parses it.
/// </summary>
/// <param name="Reflection"><c>reflection</c>: false builds without reflection, as <c>--deny-reflection</c> does.</param>
public sealed record TargetSettings(NativeTarget Target, bool Reflection);

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

    /// <summary>
    ///     <c>[lib] kind</c>: <c>"static"</c> or <c>"dynamic"</c> builds the
    ///     library as a native one C links, as well as Sable's; null for Sable's only.
    /// </summary>
    public LibraryKind? LibKind { get; init; }

    public IReadOnlyList<BinTarget> Bins { get; init; } = [];
    public IReadOnlyList<DependencySpec> Dependencies { get; init; } = [];
    public IReadOnlyList<DependencySpec> DevDependencies { get; init; } = [];
    public IReadOnlyList<DependencySpec> BuildDependencies { get; init; } = [];

    /// <summary>Each feature and what it turns on, as written.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Features { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public Profile Dev { get; init; } = Profile.Dev;
    public Profile Release { get; init; } = Profile.Release;

    /// <summary>The package's own profiles (<c>[profile.&lt;name&gt;]</c> with <c>inherits</c>), by name.</summary>
    public IReadOnlyDictionary<string, Profile> Profiles { get; init; } = new Dictionary<string, Profile>();

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

    /// <summary><c>[targets.*]</c>: each target's settings, in the order written.</summary>
    public IReadOnlyList<TargetSettings> Targets { get; init; } = [];

    /// <summary>The settings for a build for <paramref name="target" />, if the manifest has any.</summary>
    public TargetSettings? TargetSettingsFor(NativeTarget target) =>
        Targets.FirstOrDefault(t => t.Target.Platform == target.Platform && t.Target.Arch == target.Arch);

    /// <summary>What was accepted but means nothing to this toolchain: unknown keys, settings not yet acted on.</summary>
    public IReadOnlyList<ManifestError> Warnings { get; init; } = [];

    /// <summary>Whether a setting or dependency is taken from the workspace (<c>workspace = true</c>), so the file alone does not say it.</summary>
    public bool InheritsFromWorkspace { get; init; }

    /// <summary>Whether a manifest's text takes anything from its workspace, to know before reading it whether the workspace is needed.</summary>
    public static bool UsesInheritance(string text) => WorkspaceTrue().IsMatch(text);

    [GeneratedRegex(@"(^|[\s{,.])workspace\s*=\s*true", RegexOptions.Multiline)]
    private static partial Regex WorkspaceTrue();

    /// <summary>The profile a build asks for by name (<c>--profile</c>; <c>--release</c> is "release").</summary>
    /// <exception cref="PackageException">The manifest has no profile of that name.</exception>
    public Profile ProfileNamed(string name) =>
        name switch {
            "dev" => Dev,
            "release" => Release,
            _ when Profiles.TryGetValue(name, out var profile) => profile,
            _ => throw new PackageException(
                $"there is no profile '{name}': {File} has {string.Join(", ", ["dev", "release", ..Profiles.Keys.Order(StringComparer.Ordinal)])} (a profile of its own is [profile.{name}] with inherits = \"dev\" or \"release\")"),
        };

    /// <summary>
    ///     This manifest, read from the canonical TOML a <c>package.sb</c>
    ///     evaluated to, as the file it came from: its warnings name that file,
    ///     since the TOML's lines mean nothing to its author.
    /// </summary>
    internal Manifest Relocated(string file) => new() {
        File = file,
        Package = Package,
        LibPath = LibPath,
        LibKind = LibKind,
        Bins = Bins,
        Dependencies = Dependencies,
        DevDependencies = DevDependencies,
        BuildDependencies = BuildDependencies,
        Features = Features,
        Dev = Dev,
        Release = Release,
        Profiles = Profiles,
        Workspace = Workspace,
        Build = Build,
        RegistryPath = RegistryPath,
        Lints = Lints,
        GeneratorInputs = GeneratorInputs,
        Targets = Targets,
        Warnings = [..Warnings.Select(w => w with { File = file, Line = 1 })],
        InheritsFromWorkspace = InheritsFromWorkspace,
    };

    public sealed record PackageInfo(
        string Name,
        SemVersion Version,
        string Edition,
        IReadOnlyList<string> Authors,
        string? License,
        string? Description,
        string? Repository) {
        /// <summary><c>readme</c>: the file, relative to the package, that describes it; published whatever <see cref="Include" /> says.</summary>
        public string? Readme { get; init; }

        /// <summary><c>keywords</c>: up to five words <c>sable search</c> finds the package by.</summary>
        public IReadOnlyList<string> Keywords { get; init; } = [];

        /// <summary><c>include</c>: globs of the files <c>sable publish</c> uploads; with none, every file.</summary>
        public IReadOnlyList<string> Include { get; init; } = [];

        /// <summary><c>exclude</c>: globs of files <c>sable publish</c> leaves out.</summary>
        public IReadOnlyList<string> Exclude { get; init; } = [];

        /// <summary><c>publish = false</c>: <c>sable publish</c> refuses the package.</summary>
        public bool Publish { get; init; } = true;
    }

    /// <summary>The <c>[package]</c> settings besides its name, which are also what a member may take from <c>[workspace.package]</c>.</summary>
    static readonly string[] PackageSettings = ["version", "edition", "authors", "license", "description", "repository", "readme", "keywords", "include", "exclude", "publish"];

    [GeneratedRegex("^([a-z][a-z0-9_-]*/)?[a-z][a-z0-9_-]*$")]
    private static partial Regex PackageName();

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,23}$")]
    private static partial Regex Keyword();

    [GeneratedRegex("^[a-z][a-z0-9_-]*$")]
    private static partial Regex ProfileName();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_+.-]*$")]
    private static partial Regex FeatureName();

    /// <summary>Whether <paramref name="name" /> is a package name: lower-case, <c>org/name</c> allowed.</summary>
    public static bool IsValidPackageName(string name) => PackageName().IsMatch(name);

    /// <summary>Reads the manifest at <paramref name="path" />.</summary>
    /// <exception cref="ManifestException">It is not a valid manifest.</exception>
    public static Manifest Load(string path) => Parse(System.IO.File.ReadAllText(path), path);

    /// <param name="inheritance">
    ///     The workspace a member takes <c>workspace = true</c> settings from;
    ///     a manifest that is its own workspace takes them from itself.
    /// </param>
    /// <exception cref="ManifestException">It is not a valid manifest.</exception>
    public static Manifest Parse(string text, string file = "sable.toml", WorkspaceInheritance? inheritance = null) {
        TomlTable root;
        try {
            root = TomlReader.Parse(text, file);
        } catch (TomlException e) {
            throw new ManifestException([new(file, e.Position.Line, "(syntax)", e.Reason)]);
        }

        return new Reader(file, inheritance).Read(root);
    }

    /// <summary>Turns a TOML table into a manifest, collecting every error on the way.</summary>
    sealed class Reader(string file, WorkspaceInheritance? inheritance) {
        readonly List<ManifestError> _errors = [];
        readonly List<ManifestError> _warnings = [];

        /// <summary>Where <c>workspace = true</c> settings come from, once known: given, or this manifest's own <c>[workspace]</c>.</summary>
        WorkspaceInheritance? _inheritance = inheritance;

        bool _inherits;

        void Error(TomlTable table, string key, string path, string message) => _errors.Add(new(file, table.KeyPosition(key).Line, path, message));

        void Error(TomlValue value, string path, string message) => _errors.Add(new(file, value.Position.Line, path, message));

        void Warn(TomlTable table, string key, string path, string message) => _warnings.Add(new(file, table.KeyPosition(key).Line, path, message));

        /// <summary>Warns about each key of <paramref name="table" /> that is not one of <paramref name="known" />: nothing is ignored without a word.</summary>
        void WarnUnknown(TomlTable table, string path, params string[] known) {
            foreach (var key in table.Keys.Where(k => !known.Contains(k))) {
                Warn(table, key, $"{path}.{key}", "unknown key, ignored");
            }
        }

        public Manifest Read(TomlTable root) {
            string[] known = ["package", "lib", "bin", "dependencies", "dev-dependencies", "build-dependencies", "features", "profile", "workspace", "targets", "build", "registry", "lints", "generators"];
            foreach (var key in root.Keys.Where(k => !known.Contains(k))) {
                switch (key) {
                    case "target":
                        // Cargo's [target.'cfg(...)'.dependencies]: ignoring it would build without them.
                        Error(root, key, key, "there are no platform-specific dependency tables: give the dependency `when = \"os(linux)\"` (or `when: .os(.linux)` in package.sb)");
                        break;
                    case "reflect":
                        // Ignoring it would leave a type the manifest meant to
                        // be reflected on without its information.
                        Error(root, key, key,
                            "there is no [reflect] table: a type is made reflectable in code, with @reflectable or `extension T: Reflectable {}`, which works for another package's type too (docs/06 §6.3)");
                        break;
                    default:
                        Warn(root, key, key, "unknown manifest key, ignored");
                        break;
                }
            }

            var targets = new List<TargetSettings>();
            if (Table(root, "targets", "targets") is { } targetTable) {
                foreach (var triple in targetTable.Keys) {
                    if (Table(targetTable, triple, $"targets.{triple}") is not { } settings) {
                        continue;
                    }

                    if (NativeTarget.Parse(triple, out var tripleError) is not { } parsed) {
                        Error(targetTable, triple, $"targets.{triple}", tripleError ?? "not a target triple");
                        continue;
                    }

                    var reflection = true;
                    foreach (var key in settings.Keys) {
                        switch (key) {
                            case "reflection" when settings[key] is TomlBoolean { Value: var allowed }:
                                reflection = allowed;
                                break;
                            case "reflection":
                                Error(settings, key, $"targets.{triple}.reflection", "must be true or false");
                                break;
                            case "runtime":
                                // A smaller runtime asked for and not given would
                                // be a binary larger than the target allows.
                                Error(settings, key, $"targets.{triple}.runtime", "there are no runtime profiles: every build links the whole runtime (docs/06 §6.1)");
                                break;
                            default:
                                Warn(settings, key, $"targets.{triple}.{key}", "unknown key, ignored");
                                break;
                        }
                    }

                    targets.Add(new(parsed, reflection));
                }
            }

            // The workspace first: a package that is its own workspace's root
            // may take its settings from it.
            var workspace = Table(root, "workspace", "workspace") is { } w ? ReadWorkspace(w) : null;
            if (workspace is not null) {
                _inheritance ??= new(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".", file, workspace);
            }

            var package = Table(root, "package", "package") is { } p ? ReadPackage(Inherited(p)) : null;
            if (package is null && workspace is null && _errors.Count == 0) {
                _errors.Add(new(file, 1, "package", "a manifest needs a [package] table, or a [workspace] one"));
            }

            string? libPath = null;
            LibraryKind? libKind = null;
            if (Table(root, "lib", "lib") is { } lib) {
                libPath = String(lib, "path", "lib.path");
                switch (String(lib, "kind", "lib.kind")) {
                    case null:
                        break;
                    case "static":
                        libKind = LibraryKind.Static;
                        break;
                    case "dynamic":
                        libKind = LibraryKind.Dynamic;
                        break;
                    default:
                        Error(lib, "kind", "lib.kind", "must be \"static\" or \"dynamic\"");
                        break;
                }

                foreach (var key in lib.Keys.Where(static k => k is not ("path" or "kind"))) {
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
            var (dev, release, customProfiles) = ReadProfiles(root);
            var build = Table(root, "build", "build") is { } b ? ReadBuild(b) : null;
            string? registry = null;
            if (Table(root, "registry", "registry") is { } r) {
                WarnUnknown(r, "registry", "path");
                registry = String(r, "path", "registry.path");
            }

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

            List<string> generatorInputs = [];
            if (Table(root, "generators", "generators") is { } g) {
                WarnUnknown(g, "generators", "inputs");
                generatorInputs = Strings(g, "inputs", "generators.inputs") ?? [];
            }

            if (_errors.Count > 0) {
                throw new ManifestException(_errors);
            }

            return new() {
                File = file,
                Package = package,
                LibPath = libPath,
                LibKind = libKind,
                Bins = bins,
                Targets = targets,
                Dependencies = dependencies,
                DevDependencies = devDependencies,
                BuildDependencies = buildDependencies,
                Features = features,
                Dev = dev,
                Release = release,
                Profiles = customProfiles,
                Workspace = workspace,
                Build = build,
                RegistryPath = registry,
                Lints = lints,
                GeneratorInputs = generatorInputs,
                Warnings = _warnings,
                InheritsFromWorkspace = _inherits,
            };
        }

        /// <summary>
        ///     <c>[package]</c> with each <c>key.workspace = true</c> replaced by
        ///     the workspace's <c>[workspace.package]</c> value for the key.
        /// </summary>
        TomlTable Inherited(TomlTable table) {
            if (!table.Entries.Any(static e => IsInherited(e.Value))) {
                return table;
            }

            var resolved = new TomlTable { Position = table.Position };
            foreach (var (key, value) in table.Entries) {
                if (!IsInherited(value)) {
                    resolved.Add(key, value, table.KeyPosition(key));
                    continue;
                }

                _inherits = true;
                if (_inheritance?.Workspace.Package?[key] is { } shared) {
                    resolved.Add(key, shared, table.KeyPosition(key));
                } else {
                    Error(table, key, $"package.{key}", _inheritance is null
                        ? "takes its value from the workspace, but no workspace above lists this package as a member"
                        : $"takes its value from the workspace, and {_inheritance.File} has no '{key}' under [workspace.package]");
                }
            }

            return resolved;
        }

        /// <summary>A value written <c>{ workspace = true }</c>, or <c>key.workspace = true</c>.</summary>
        static bool IsInherited(TomlValue value) => value is TomlTable { Count: 1 } table && table["workspace"] is TomlBoolean { Value: true };

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
            foreach (var key in table.Keys.Where(k => k != "name" && !PackageSettings.Contains(k))) {
                Warn(table, key, $"package.{key}", key switch {
                    "categories" => "there are no registry categories: say what the package is about with 'keywords'; ignored",
                    "homepage" or "documentation" => "the registry records a package's 'repository' and nothing else to link to; ignored",
                    _ => "unknown key, ignored",
                });
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
            var readme = String(table, "readme", "package.readme");
            if (readme is { } readmePath && !IsRelativeWithin(readmePath)) {
                Error(table, "readme", "package.readme", $"'{readmePath}' is not a path inside the package");
            }

            var keywords = Strings(table, "keywords", "package.keywords") ?? [];
            foreach (var keyword in keywords.Where(static k => !Keyword().IsMatch(k))) {
                Error(table, "keywords", "package.keywords", $"'{keyword}' is not a keyword: lower-case letters, digits, '-' and '_', starting with a letter, at most 24 characters");
            }

            if (keywords.Count > 5) {
                Error(table, "keywords", "package.keywords", $"a package has at most 5 keywords, and this one has {keywords.Count}");
            }

            var include = Patterns(table, "include");
            var exclude = Patterns(table, "exclude");
            var publish = Bool(table, "publish", "package.publish") ?? true;
            return name is null || version is null
                ? null
                : new(name, version, edition, authors, license, description, repository) {
                    Readme = readme,
                    Keywords = keywords,
                    Include = include,
                    Exclude = exclude,
                    Publish = publish,
                };
        }

        /// <summary><c>include</c> or <c>exclude</c>: globs of the package's files, each checked to be one.</summary>
        List<string> Patterns(TomlTable table, string key) {
            var patterns = Strings(table, key, $"package.{key}") ?? [];
            foreach (var pattern in patterns) {
                if (FileGlob.Problem(pattern) is { } problem) {
                    Error(table, key, $"package.{key}", $"'{pattern}': {problem}");
                }
            }

            return patterns;
        }

        static bool IsRelativeWithin(string path) =>
            path.Length > 0 && !Path.IsPathRooted(path) && !path.Replace('\\', '/').Split('/').Contains("..");

        WorkspaceSpec ReadWorkspace(TomlTable table) {
            foreach (var key in table.Keys.Where(static k => k is not ("members" or "exclude" or "package" or "dependencies"))) {
                Warn(table, key, $"workspace.{key}", key switch {
                    "metadata" => "nothing reads workspace metadata; ignored",
                    // Cargo's resolver = "2": there is one way to resolve.
                    "resolver" => "there is one resolver, PubGrub with features unified across the graph (docs/05 §5.3); ignored",
                    _ => "unknown key, ignored",
                });
            }

            // What members may take: the [package] keys, each as its own
            // [package] would have it, and dependencies as a member writes them.
            var package = Table(table, "package", "workspace.package");
            foreach (var key in package?.Keys.Where(k => !PackageSettings.Contains(k)) ?? []) {
                Error(package!, key, $"workspace.package.{key}", key == "name"
                    ? "each member names itself; a name cannot be shared"
                    : "is not a setting members can take from the workspace");
            }

            if (package?["version"] is TomlString { Value: var version } && !SemVersion.TryParse(version, out _, out var problem)) {
                Error(package, "version", "workspace.package.version", $"'{version}' is not a semantic version: {problem}");
            }

            var dependencies = ReadDependencies(table, "dependencies", "workspace.dependencies");
            foreach (var dependency in dependencies.Where(static d => d.Optional)) {
                Error(table, "dependencies", $"workspace.dependencies.{dependency.Name}", "whether a dependency is optional is the member's to say");
            }

            return new(Strings(table, "members", "workspace.members") ?? [], Strings(table, "exclude", "workspace.exclude") ?? []) {
                Package = package,
                Dependencies = dependencies,
            };
        }

        List<DependencySpec> ReadDependencies(TomlTable root, string section, string? fullName = null) {
            var result = new List<DependencySpec>();
            fullName ??= section;
            if (Table(root, section, fullName) is not { } table) {
                return result;
            }

            foreach (var (name, value) in table.Entries) {
                var path = $"{fullName}.{name}";
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
                    case TomlTable detail when detail["workspace"] is TomlBoolean { Value: true } && fullName != "workspace.dependencies":
                        if (Inherit(table, name, path, detail, line) is { } inherited) {
                            result.Add(inherited);
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
                                "workspace" => fullName == "workspace.dependencies"
                                    ? "a workspace's own dependency cannot take itself from the workspace"
                                    : "takes the dependency from the workspace only as 'workspace = true'",
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

        /// <summary>
        ///     <c>name = { workspace = true }</c>: the workspace's dependency of
        ///     that name, its path made relative to this manifest. The member may
        ///     add features, say it is optional, and say when it applies.
        /// </summary>
        DependencySpec? Inherit(TomlTable table, string name, string path, TomlTable detail, int line) {
            _inherits = true;
            foreach (var key in detail.Keys.Where(static k => k is not ("workspace" or "features" or "optional" or "when"))) {
                Error(detail, key, $"{path}.{key}", $"comes from the workspace, which says '{key}' for it");
            }

            if (_inheritance is null) {
                Error(table, name, path, "takes the dependency from the workspace, but no workspace above lists this package as a member");
                return null;
            }

            if (_inheritance.Workspace.Dependencies.FirstOrDefault(d => d.Name == name) is not { } shared) {
                Error(table, name, path, $"takes the dependency from the workspace, and {_inheritance.File} has no '{name}' under [workspace.dependencies]");
                return null;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
            var relocated = shared.Path is { } sharedPath
                ? Path.GetRelativePath(directory, Path.GetFullPath(Path.Combine(_inheritance.Directory, sharedPath))).Replace('\\', '/')
                : null;
            var when = String(detail, "when", $"{path}.when") ?? shared.When;
            if (when is not null && !DependencyCondition.IsValid(when)) {
                Error(detail, "when", $"{path}.when", $"'{when}' is not a condition: os(macos), os(linux), os(windows), arch(arm64) or arch(x64)");
            }

            return shared with {
                Path = relocated,
                Optional = Bool(detail, "optional", $"{path}.optional") ?? false,
                Features = [..shared.Features.Union(Strings(detail, "features", $"{path}.features") ?? [], StringComparer.Ordinal)],
                Line = line,
                When = when,
            };
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

        /// <summary>
        ///     <c>[profile.dev]</c>, <c>[profile.release]</c>, and profiles of
        ///     the package's own (<c>[profile.bench-fast]</c>), each of which
        ///     <c>inherits</c> dev's or release's settings, as the manifest
        ///     has them, and changes some.
        /// </summary>
        (Profile Dev, Profile Release, Dictionary<string, Profile> Custom) ReadProfiles(TomlTable root) {
            var dev = Profile.Dev;
            var release = Profile.Release;
            var custom = new Dictionary<string, Profile>(StringComparer.Ordinal);
            if (Table(root, "profile", "profile") is not { } profiles) {
                return (dev, release, custom);
            }

            var tables = new List<(string Name, TomlTable Table)>();
            foreach (var (name, value) in profiles.Entries) {
                if (value is not TomlTable table) {
                    Error(profiles, name, $"profile.{name}", $"must be a table, not {value.Kind}");
                } else if (name is not ("dev" or "release") && ProfileNameProblem(name) is { } problem) {
                    Error(profiles, name, $"profile.{name}", problem);
                } else {
                    tables.Add((name, table));
                }
            }

            // Dev and release first: the others start from them as written.
            foreach (var (name, table) in tables.Where(static t => t.Name is "dev" or "release")) {
                if (table["inherits"] is not null) {
                    Error(table, "inherits", $"profile.{name}.inherits", $"'{name}' is a profile of its own, and inherits nothing");
                }

                if (name == "dev") {
                    dev = ReadProfile(table, dev);
                } else {
                    release = ReadProfile(table, release);
                }
            }

            foreach (var (name, table) in tables.Where(static t => t.Name is not ("dev" or "release"))) {
                var basis = String(table, "inherits", $"profile.{name}.inherits") switch {
                    "dev" => dev,
                    "release" => release,
                    null when table["inherits"] is null => Invalid<Profile?>(null, table, $"profile.{name}", "a profile of the package's own says which it starts from: inherits = \"dev\" or \"release\""),
                    null => null,
                    var other => Invalid<Profile?>(null, table["inherits"]!, $"profile.{name}.inherits", $"a profile inherits \"dev\" or \"release\", not \"{other}\""),
                };
                if (basis is not null) {
                    custom[name] = ReadProfile(table, basis with { Name = name });
                }
            }

            return (dev, release, custom);
        }

        /// <summary>Why a profile of the package's own cannot be called <paramref name="name" />; null when it can.</summary>
        static string? ProfileNameProblem(string name) =>
            !ProfileName().IsMatch(name) ? $"'{name}' is not a profile name: lower-case letters, digits, '-' and '_', starting with a letter"
            // Its output goes to target/<name>/, beside what these are.
            : name is "check" or "doc" or "harness" or "coverage" or "bench" or "fuzz" or "generated" ? $"'{name}' is a directory of target/ already; call the profile something else"
            : null;

        T Invalid<T>(T result, TomlValue value, string path, string message) {
            Error(value, path, message);
            return result;
        }

        Profile ReadProfile(TomlTable table, Profile defaults) {
            var profile = defaults;
            // What the profile inherits was written too, for what it says it cannot do.
            var recorded = new Dictionary<string, string>(defaults.Recorded, StringComparer.Ordinal);
            foreach (var (key, value) in table.Entries) {
                var path = $"profile.{defaults.Name}.{key}";
                switch (key) {
                    case "inherits":
                        // Read by ReadProfiles.
                        break;
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

                        // DWARF is emitted whole or not at all: a part of it
                        // asked for is more than was asked for, said so.
                        if (value is TomlInteger { Value: 1 } or TomlString { Value: "line-tables-only" or "limited" }) {
                            Warn(table, key, path, "debug information is emitted in full or not at all; built with all of it");
                        }

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
                            "debug-assertions" => profile with { DebugAssertionsSetting = flag.Value },
                            "incremental" => profile with { Incremental = flag.Value },
                            _ => profile,
                        };
                        recorded[key] = flag.Value ? "true" : "false";
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

                        // The units a build compiles are its packages, however
                        // many there are (docs/05 §5.4): a count is not
                        // something it can follow, only 1, compile it whole.
                        if (value is TomlInteger { Value: > 1 and <= 256 }) {
                            Warn(table, key, path, "a build compiles one unit per package, or the program whole with codegen-units = 1; any other count has no effect");
                        }

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
            WarnUnknown(table, "build", "script", "allow-net", "allow-exec", "allow-read", "allow-write");
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
