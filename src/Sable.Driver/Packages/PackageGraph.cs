namespace Sable.Driver.Packages;

/// <summary>
///     Where package commands find the registry, the cache and the publishing
///     key, and where they write. <see cref="FromProcess" /> reads
///     <c>SABLE_HOME</c> (default <c>~/.sable</c>) and <c>SABLE_REGISTRY</c>;
///     tests make their own, in temporary directories.
/// </summary>
public sealed record PackageEnvironment(string RegistryDirectory, string CacheDirectory, string KeyPath, TextWriter Out, TextWriter Error) {
    public static PackageEnvironment FromProcess() {
        var home = Environment.GetEnvironmentVariable("SABLE_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sable");
        var registry = Environment.GetEnvironmentVariable("SABLE_REGISTRY") is { Length: > 0 } r ? r : Path.Combine(home, "registry");
        return new(registry, Path.Combine(home, "cache"), Path.Combine(home, "key"), Console.Out, Console.Error);
    }

    /// <summary>Everything under one directory: <c>registry/</c>, <c>cache/</c> and <c>key</c>.</summary>
    public static PackageEnvironment At(string home, TextWriter? output = null, TextWriter? error = null) =>
        new(Path.Combine(home, "registry"), Path.Combine(home, "cache"), Path.Combine(home, "key"), output ?? TextWriter.Null, error ?? TextWriter.Null);

    /// <summary>The registry a workspace uses: its <c>[registry] path</c>, else this environment's.</summary>
    public Registry RegistryFor(Workspace workspace) =>
        new(workspace.RootManifest.RegistryPath is { } path ? Path.GetFullPath(Path.Combine(workspace.Root, path)) : RegistryDirectory);
}

/// <summary>Which features to turn on in the packages being built (<c>--features</c>, <c>--no-default-features</c>, <c>--all-features</c>).</summary>
public sealed record FeatureRequest(IReadOnlyList<string> Features, bool NoDefaultFeatures = false, bool AllFeatures = false) {
    public static FeatureRequest Default { get; } = new([]);
}

/// <summary>How to resolve.</summary>
/// <param name="Update">null to keep the lock file's versions where they fit; empty to re-resolve everything; else the packages to re-resolve.</param>
/// <param name="Locked">Fail rather than change the lock file.</param>
/// <param name="Packages">The members being built, whose features <paramref name="Features" /> selects; null for all.</param>
/// <param name="Target">
///     What is built for — the <c>os(...)</c> and <c>arch(...)</c> a
///     dependency's <c>when:</c> tests — as <c>#if</c> names them; null for
///     this machine. The lock file keeps every dependency either way.
/// </param>
public sealed record ResolveOptions(
    FeatureRequest? Features = null,
    IReadOnlySet<string>? Update = null,
    bool Locked = false,
    IReadOnlyList<string>? Packages = null,
    (string Os, string Arch)? Target = null);

/// <summary>A package a build uses, at the version chosen, with the features turned on and where its source is.</summary>
public sealed record ResolvedPackage(
    LocalPackage Package,
    bool IsLocal,
    string? Checksum,
    IReadOnlySet<string> Features,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> DevDependencies,
    GitCheckout? Git = null) {
    public string Name => Package.Name;
    public SemVersion Version => Package.Version;
}

/// <summary>
///     A workspace with its dependencies resolved, the lock file brought up to
///     date, and every package's source on disk — what a build compiles.
/// </summary>
public sealed partial class PackageGraph {
    readonly SourceStore _store;

    PackageGraph(Workspace workspace, IReadOnlyDictionary<string, ResolvedPackage> packages, LockFile lockFile, SourceStore store) {
        Workspace = workspace;
        Packages = packages;
        Lock = lockFile;
        _store = store;
    }

    /// <summary>The source directory of a locked registry package, fetched if it is not on disk.</summary>
    public string Fetch(LockedPackage package) => _store.Fetch(package.Name, package.Version, package.Checksum);

    public Workspace Workspace { get; }

    /// <summary>The packages, by name: members, path dependencies and registry packages.</summary>
    public IReadOnlyDictionary<string, ResolvedPackage> Packages { get; }

    public LockFile Lock { get; }

    /// <summary>The name a resolution gives the workspace when it has several members.</summary>
    public const string WorkspaceRoot = "(workspace)";

    /// <summary>
    ///     Resolves a workspace: brings <c>sable.lock</c> up to date (writing it
    ///     when it changed), chooses the features to build with, and finds or
    ///     fetches each package's source — from <c>vendor/</c>, the cache, or
    ///     the registry, checking checksums and signatures on the way.
    /// </summary>
    /// <exception cref="ResolutionException">No versions satisfy the requirements; the message explains why.</exception>
    /// <exception cref="PackageException">Something else is in the way.</exception>
    public static PackageGraph Resolve(Workspace workspace, PackageEnvironment environment, ResolveOptions? options = null) {
        options ??= new();
        var previous = LockFile.Load(workspace.LockPath);
        var git = new GitSource(environment.CacheDirectory,environment.Out, previous, options.Update);
        var locals = workspace.LocalPackages(git);
        var registry = environment.RegistryFor(workspace);
        var store = new SourceStore(workspace, environment, registry, previous);
        var source = new GraphSource(workspace, locals, registry, previous, store, git.Checkouts);

        // The lock file holds what every feature of every member could need.
        var preferred = new Dictionary<string, SemVersion>(StringComparer.Ordinal);
        foreach (var held in previous?.Packages ?? []) {
            if (options.Update is null || (options.Update.Count > 0 && !options.Update.Contains(held.Name))) {
                preferred[held.Name] = held.Version;
            }
        }

        var (root, rootVersion) = source.SetRoot([..workspace.Members.Select(static m => (m, AllFeatures(m)))]);
        IReadOnlyDictionary<string, SemVersion> full;
        try {
            full = ResolveWithWeakFeatures(source, root, rootVersion, preferred);
        } catch (ResolutionException e) when (!registry.IsAvailable) {
            throw new ResolutionException($"{e.Message}\nnote: the registry at '{registry.Directory}' cannot be read, so only versions in the cache and vendor/ were considered\n", e.Incompatibility);
        }

        var lockFile = source.LockFor(full, root);
        var text = lockFile.Write();
        if (previous is null || previous.Write() != text) {
            if (options.Locked) {
                throw new PackageException($"{Workspace.LockName} needs to change, and --locked forbids it: run 'sable update' or build without --locked");
            }

            File.WriteAllText(workspace.LockPath, text);
        }

        // The build itself: the features asked for, at the versions locked.
        var selected = options.Packages is { } names ? names.Select(workspace.Member).ToList() : [..workspace.Members];
        var request = options.Features ?? FeatureRequest.Default;
        var requests = workspace.Members.Select(m => (m, selected.Contains(m) ? Requested(m, request) : Requested(m, FeatureRequest.Default))).ToList();
        (root, rootVersion) = source.SetRoot(requests);
        var locked = lockFile.Packages.ToDictionary(static p => p.Name, static p => p.Version, StringComparer.Ordinal);
        var build = ResolveWithWeakFeatures(source, root, rootVersion, locked);
        return new(workspace, source.Packages(build, root, lockFile, options.Target ?? (Sable.Syntax.ParseOptions.HostOs, Sable.Syntax.ParseOptions.HostArch)), lockFile, store);
    }

    /// <summary>
    ///     Resolves, then turns on what weak features (<c>dep?/feature</c>)
    ///     ask of the dependencies that ended up on, and resolves again, until
    ///     there are none left to turn on. A weak feature depends on whether
    ///     its dependency is on at all, which is a result of resolution, not
    ///     an input PubGrub can be given; so it is applied after, as a
    ///     requirement of the root at the version already chosen.
    /// </summary>
    static IReadOnlyDictionary<string, SemVersion> ResolveWithWeakFeatures(
        GraphSource source, string root, SemVersion rootVersion, IReadOnlyDictionary<string, SemVersion> preferred) {
        while (true) {
            var decisions = Resolver.Resolve(source, root, rootVersion, preferred);
            if (source.WeakFeatures(decisions) is not { Count: > 0 } weak) {
                return decisions;
            }

            source.Require(weak);
        }
    }

    /// <summary>Every feature a package has: those it declares, and its optional dependencies' implicit ones.</summary>
    static IReadOnlyList<string> AllFeatures(LocalPackage package) => [
        ..package.Manifest.Features.Keys,
        ..package.Manifest.Dependencies
            .Where(d => d.Optional && !package.Manifest.Features.ContainsKey(d.Name) && !package.Manifest.Features.Values.Any(v => v.Contains($"dep:{d.Name}")))
            .Select(static d => d.Name),
    ];

    static IReadOnlyList<string> Requested(LocalPackage package, FeatureRequest request) {
        if (request.AllFeatures) {
            return AllFeatures(package);
        }

        foreach (var feature in request.Features.Where(f => !package.Manifest.Features.ContainsKey(f) && !package.Manifest.Dependencies.Any(d => d.Optional && d.Name == f))) {
            throw new PackageException($"{package.Name} has no feature '{feature}'");
        }

        return [..request.Features, ..request.NoDefaultFeatures ? [] : new[] { "default" }];
    }

    /// <summary>The packages a member needs to build, itself first, then each dependency after everything that depends on it.</summary>
    /// <param name="dev">
    ///     For a test or bench build: the member's dev-dependencies too, and
    ///     what they depend on. Only the member's own count, as in Cargo — a
    ///     dependency's dev-dependencies are for its own tests.
    /// </param>
    public IReadOnlyList<ResolvedPackage> Closure(string member, bool dev = false) {
        var order = new List<ResolvedPackage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string name) {
            if (!seen.Add(name)) {
                return;
            }

            var package = Packages[name];
            foreach (var dependency in dev && name == member ? [..package.Dependencies, ..package.DevDependencies] : package.Dependencies) {
                Visit(dependency);
            }

            order.Add(package);
        }

        Visit(member);
        order.Reverse();
        return order;
    }
}

/// <summary>Finds a registry package's source: in <c>vendor/</c>, in the cache, or fetched from the registry into the cache.</summary>
sealed class SourceStore(Workspace workspace, PackageEnvironment environment, Registry registry, LockFile? previous) {
    public const string ChecksumFile = ".sable-checksum";

    static string DirectoryName(string name, SemVersion version) => $"{name.Replace('/', '~')}-{version}";

    string VendorDirectory(string name, SemVersion version) => Path.Combine(workspace.VendorDirectory, DirectoryName(name, version));

    string CacheDirectory(string name, SemVersion version) => Path.Combine(environment.CacheDirectory, DirectoryName(name, version));

    static bool Holds(string directory, string? checksum) =>
        File.Exists(Path.Combine(directory, ChecksumFile))
        && (checksum is null || File.ReadAllText(Path.Combine(directory, ChecksumFile)).Trim() == checksum);

    /// <summary>A package already on disk, needing no registry: vendored, or in the cache.</summary>
    public string? Present(string name, SemVersion version, string? checksum) {
        var vendored = VendorDirectory(name, version);
        if (Holds(vendored, checksum)) {
            return vendored;
        }

        var cached = CacheDirectory(name, version);
        return Holds(cached, checksum) ? cached : null;
    }

    /// <summary>The checksum the lock file has for a version, if it locks that version.</summary>
    public string? LockedChecksum(string name, SemVersion version) =>
        previous?.Find(name) is { IsRegistry: true } locked && locked.Version == version ? locked.Checksum : null;

    /// <summary>The package's source directory, fetching it when it is not on disk.</summary>
    public string Fetch(string name, SemVersion version, string? checksum) {
        if (Present(name, version, checksum) is { } present) {
            return present;
        }

        if (!registry.IsAvailable) {
            throw new PackageException($"{name} {version} is not in the cache or vendor/, and the registry at '{registry.Directory}' cannot be read");
        }

        var entry = registry.Entry(name, version) ?? throw new PackageException($"the registry has no {name} {version}");
        if (checksum is not null && entry.Checksum != checksum) {
            throw new RegistryException($"{name} {version} in the registry has checksum {entry.Checksum}, but {Workspace.LockName} has {checksum}: the registry has been tampered with, or the lock file has");
        }

        environment.Out.WriteLine($"  Downloading {name} v{version}");
        var files = Registry.Unpack(registry.Download(entry));
        var target = CacheDirectory(name, version);
        var temporary = target + $".tmp-{Guid.NewGuid():N}";
        foreach (var (path, bytes) in files) {
            var file = Path.Combine(temporary, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }

        File.WriteAllText(Path.Combine(temporary, ChecksumFile), entry.Checksum + "\n");
        if (Directory.Exists(target)) {
            Directory.Delete(target, true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(temporary, target);
        return target;
    }
}

/// <summary>
///     What the resolver sees: the workspace's own packages, the registry's,
///     and — offline — the lock file's versions found in the cache. A
///     feature is a package of its own, <c>name[feature]</c>, which depends on
///     its package at the same version and on whatever the feature turns on:
///     so features are resolved, and unified, by PubGrub like anything else.
/// </summary>
sealed class GraphSource(
    Workspace workspace,
    IReadOnlyDictionary<string, LocalPackage> locals,
    Registry registry,
    LockFile? previous,
    SourceStore store,
    IReadOnlyDictionary<string, GitCheckout> git) : IPackageSource {
    /// <summary>A version's dependencies and features, from whichever place knows them.</summary>
    sealed record Spec(IReadOnlyList<DependencySpec> Normal, IReadOnlyList<DependencySpec> Dev, IReadOnlyDictionary<string, IReadOnlyList<string>> Features);

    readonly Dictionary<string, IReadOnlyList<IndexEntry>> _entries = new(StringComparer.Ordinal);
    readonly HashSet<string> _members = [..workspace.Members.Select(static m => m.Name)];
    string _root = "";
    SemVersion _rootVersion = new(0, 0, 0);
    IReadOnlyList<(string, VersionReq)> _rootDependencies = [];
    readonly List<(string, VersionReq)> _weak = [];

    /// <summary>Makes the resolution's root: the lone member itself, or a workspace root depending on every member.</summary>
    public (string Root, SemVersion Version) SetRoot(IReadOnlyList<(LocalPackage Member, IReadOnlyList<string> Features)> members) {
        _weak.Clear();
        if (members is [var (single, features)] && !workspace.IsWorkspace) {
            _root = single.Name;
            _rootVersion = single.Version;
            _rootDependencies = [..Expand(Of(single).Normal.Concat(Of(single).Dev).Where(static d => !d.Optional)), ..features.Select(f => (FeatureId(single.Name, f), VersionReq.Exactly(single.Version)))];
        } else {
            _root = PackageGraph.WorkspaceRoot;
            _rootVersion = new(0, 0, 0);
            _rootDependencies = [
                ..members.SelectMany(m => m.Features.Select(f => (FeatureId(m.Member.Name, f), VersionReq.Exactly(m.Member.Version)))
                    .Prepend((m.Member.Name, VersionReq.Exactly(m.Member.Version)))),
            ];
        }

        return (_root, _rootVersion);
    }

    public static string FeatureId(string package, string feature) => $"{package}[{feature}]";

    static (string Name, string? Feature) Split(string id) {
        var open = id.IndexOf('[', StringComparison.Ordinal);
        return open < 0 ? (id, null) : (id[..open], id[(open + 1)..^1]);
    }

    /// <summary>
    ///     A package as an explanation names it. Depending on <c>x[default]</c>
    ///     at some versions is depending on <c>x</c> at them, so it is called <c>x</c>.
    /// </summary>
    public string Display(string package) =>
        package == PackageGraph.WorkspaceRoot ? "the workspace"
        : package.EndsWith("[default]", StringComparison.Ordinal) ? package[..^"[default]".Length]
        : package;

    IReadOnlyList<IndexEntry> Entries(string name) {
        if (!_entries.TryGetValue(name, out var entries)) {
            _entries[name] = entries = registry.IsAvailable ? registry.Entries(name) : [];
        }

        return entries;
    }

    LockedPackage? Locked(string name) => previous?.Find(name) is { IsRegistry: true } locked ? locked : null;

    public bool Exists(string package) {
        var (name, _) = Split(package);
        return package == _root || locals.ContainsKey(name) || Entries(name).Count > 0 || Locked(name) is not null;
    }

    IReadOnlyList<SemVersion> BaseVersions(string name) {
        if (locals.TryGetValue(name, out var local)) {
            return [local.Version];
        }

        var entries = Entries(name);
        var locked = Locked(name);
        if (entries.Count > 0) {
            // A yanked version stays usable by a lock file that already has it.
            return [..entries.Where(e => !e.Yanked || e.Version == locked?.Version).Select(static e => e.Version)];
        }

        // Offline: what the lock file holds, if its source is on disk.
        return locked is not null && store.Present(name, locked.Version, locked.Checksum) is not null ? [locked.Version] : [];
    }

    public IReadOnlyList<SemVersion> Versions(string package) {
        if (package == _root) {
            return [_rootVersion];
        }

        var (name, feature) = Split(package);
        var versions = BaseVersions(name);
        return feature is null or "default" ? versions : [..versions.Where(v => HasFeature(Of(name, v), feature))];
    }

    static bool HasFeature(Spec spec, string feature) =>
        spec.Features.ContainsKey(feature)
        || (spec.Normal.Any(d => d.Optional && d.Name == feature) && !spec.Features.Values.Any(vs => vs.Contains($"dep:{feature}")));

    public IReadOnlyList<(string Package, VersionReq Requirement)> Dependencies(string package, SemVersion version) {
        if (package == _root) {
            return [.._rootDependencies, .._weak];
        }

        var (name, feature) = Split(package);
        var spec = Of(name, version);
        if (feature is null) {
            var dependencies = spec.Normal.AsEnumerable();
            if (_members.Contains(name)) {
                dependencies = dependencies.Concat(spec.Dev);
            }

            return [..Expand(dependencies.Where(static d => !d.Optional))];
        }

        var result = new List<(string, VersionReq)> { (name, VersionReq.Exactly(version)) };
        if (!spec.Features.TryGetValue(feature, out var values)) {
            // "default" when there is none, or an optional dependency's implicit feature.
            values = feature == "default" ? [] : [$"dep:{feature}"];
        }

        foreach (var value in values) {
            result.AddRange(FeatureDependencies(name, version, spec, value));
        }

        return result;
    }

    IEnumerable<(string, VersionReq)> FeatureDependencies(string name, SemVersion version, Spec spec, string value) {
        DependencySpec? Find(string dependency) => spec.Normal.FirstOrDefault(d => d.Name == dependency);

        if (value.StartsWith("dep:", StringComparison.Ordinal)) {
            return Find(value[4..]) is { } d ? Expand([d]) : [];
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0) {
            var dependencyName = value[..slash];
            var subfeature = value[(slash + 1)..];

            // "dep?/feature" is weak: it does not turn the dependency on, and
            // turns the feature on only if something else does (WeakFeatures).
            if (dependencyName.EndsWith('?') || Find(dependencyName) is not { } dependency) {
                return [];
            }

            return [..(dependency.Optional ? Expand([dependency]) : []), (FeatureId(dependency.Name, subfeature), dependency.Requirement)];
        }

        if (spec.Features.ContainsKey(value)) {
            return [(FeatureId(name, value), VersionReq.Exactly(version))];
        }

        return Find(value) is { Optional: true } optional ? Expand([optional]) : [];
    }

    /// <summary>
    ///     The features weak values (<c>dep?/feature</c>) of the features on
    ///     ask for, of dependencies that are on, and that are not on yet.
    /// </summary>
    public IReadOnlyList<(string Package, VersionReq Requirement)> WeakFeatures(IReadOnlyDictionary<string, SemVersion> decisions) {
        var needed = new List<(string, VersionReq)>();
        foreach (var (name, version) in Real(decisions, _root)) {
            var spec = Of(name, version);
            List<string>? enabled = null;
            foreach (var feature in FeaturesOf(name, decisions)) {
                if (!spec.Features.TryGetValue(feature, out var values)) {
                    continue;
                }

                foreach (var value in values) {
                    var slash = value.IndexOf('/', StringComparison.Ordinal);
                    if (slash < 2 || value[slash - 1] != '?') {
                        continue;
                    }

                    var dependency = value[..(slash - 1)];
                    var id = FeatureId(dependency, value[(slash + 1)..]);
                    enabled ??= EnabledDependencies(name, version, decisions, true);
                    if (enabled.Contains(dependency) && !decisions.ContainsKey(id) && !needed.Any(n => n.Item1 == id)) {
                        needed.Add((id, VersionReq.Exactly(decisions[dependency])));
                    }
                }
            }
        }

        return needed;
    }

    /// <summary>Makes the root require these too, for the rest of this resolution.</summary>
    public void Require(IEnumerable<(string Package, VersionReq Requirement)> requirements) => _weak.AddRange(requirements);

    /// <summary>A dependency as the resolver sees it: the package, its default features, and the features asked of it.</summary>
    static IEnumerable<(string, VersionReq)> Expand(IEnumerable<DependencySpec> dependencies) {
        foreach (var d in dependencies) {
            yield return (d.Name, d.Requirement);
            if (d.DefaultFeatures) {
                yield return (FeatureId(d.Name, "default"), d.Requirement);
            }

            foreach (var feature in d.Features) {
                yield return (FeatureId(d.Name, feature), d.Requirement);
            }
        }
    }

    static Spec Of(LocalPackage package) => new(package.Manifest.Dependencies, package.Manifest.DevDependencies, package.Manifest.Features);

    Spec Of(string name, SemVersion version) {
        if (locals.TryGetValue(name, out var local)) {
            return Of(local);
        }

        // A registry package as its manifest says, when on disk; else as the index says.
        var entry = Entries(name).FirstOrDefault(e => e.Version == version);
        if (entry is not null) {
            return new([..entry.Specs("normal")], [..entry.Specs("dev")], entry.Features);
        }

        var directory = store.Present(name, version, Locked(name)?.Checksum)
            ?? throw new PackageException($"nothing is known of {name} {version}");
        var manifest = ManifestFiles.Load(directory);
        return new(
            [..manifest.Dependencies.Select(static d => d with { Path = null, Git = null, Reference = null })],
            [..manifest.DevDependencies.Select(static d => d with { Path = null, Git = null, Reference = null })],
            manifest.Features);
    }

    /// <summary>The lock file for a resolution: each real package, its source, checksum and dependencies.</summary>
    public LockFile LockFor(IReadOnlyDictionary<string, SemVersion> decisions, string root) {
        var packages = new List<LockedPackage>();
        foreach (var (name, version) in Real(decisions, root)) {
            var dependencies = EnabledDependencies(name, version, decisions, true)
                .Select(d => $"{d} {decisions[d]}")
                .ToList();
            if (git.TryGetValue(name, out var checkout)) {
                packages.Add(new(name, version, checkout.Source, null, dependencies));
            } else if (locals.TryGetValue(name, out var local)) {
                packages.Add(new(name, version, $"path+{workspace.Relative(local.Directory)}", null, dependencies));
            } else {
                var lockedChecksum = Locked(name) is { } held && held.Version == version ? held.Checksum : null;
                var checksum = Entries(name).FirstOrDefault(e => e.Version == version)?.Checksum ?? lockedChecksum;
                if (lockedChecksum is not null && checksum != lockedChecksum) {
                    throw new RegistryException($"{name} {version} in the registry has checksum {checksum}, but {Workspace.LockName} has {lockedChecksum}: the registry has been tampered with, or the lock file has");
                }

                packages.Add(new(name, version, "registry", checksum, dependencies));
            }
        }

        return new(packages);
    }

    static IEnumerable<(string Name, SemVersion Version)> Real(IReadOnlyDictionary<string, SemVersion> decisions, string root) =>
        decisions.Where(static d => !d.Key.Contains('[', StringComparison.Ordinal) && d.Key != PackageGraph.WorkspaceRoot)
            .Select(static d => (d.Key, d.Value));

    static IReadOnlySet<string> FeaturesOf(string name, IReadOnlyDictionary<string, SemVersion> decisions) =>
        decisions.Keys.Select(Split).Where(s => s.Name == name && s.Feature is not null).Select(static s => s.Feature!).ToHashSet(StringComparer.Ordinal);

    /// <summary>The dependencies a package has with the features chosen: its required ones, and the optional ones its features turn on.</summary>
    List<string> EnabledDependencies(string name, SemVersion version, IReadOnlyDictionary<string, SemVersion> decisions, bool includeDev, (string Os, string Arch)? target = null) {
        var spec = Of(name, version);

        // A dependency whose 'when' names another platform is resolved and
        // locked, so the lock is the same on every machine, but not built.
        bool Applies(DependencySpec d) => target is not { } built || d.AppliesTo(built.Os, built.Arch);
        var dependencies = spec.Normal.Where(d => (!d.Optional || decisions.ContainsKey(d.Name)) && Applies(d)).Select(static d => d.Name);
        if (includeDev && _members.Contains(name)) {
            dependencies = dependencies.Concat(spec.Dev.Where(Applies).Select(static d => d.Name));
        }

        // An optional dependency counts only if one of this package's features turned it on.
        var features = FeaturesOf(name, decisions);
        var turnedOn = features.SelectMany(f => spec.Features.TryGetValue(f, out var values) ? values : [$"dep:{f}"])
            .Select(static v => v.StartsWith("dep:", StringComparison.Ordinal) ? v[4..] : v.Split('/')[0])
            .ToHashSet(StringComparer.Ordinal);
        return [
            ..dependencies.Distinct()
                .Where(d => decisions.ContainsKey(d))
                .Where(d => spec.Normal.Concat(spec.Dev).First(s => s.Name == d) is not { Optional: true } || turnedOn.Contains(d))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>The packages of a build resolution, with their sources found and their features chosen.</summary>
    public IReadOnlyDictionary<string, ResolvedPackage> Packages(IReadOnlyDictionary<string, SemVersion> decisions, string root, LockFile lockFile, (string Os, string Arch) target) {
        var result = new Dictionary<string, ResolvedPackage>(StringComparer.Ordinal);
        foreach (var (name, version) in Real(decisions, root)) {
            var normal = EnabledDependencies(name, version, decisions, false, target);
            var dev = EnabledDependencies(name, version, decisions, true, target).Except(normal).ToList();
            var features = FeaturesOf(name, decisions);
            if (locals.TryGetValue(name, out var local)) {
                // A package from git is, like a registry package, a workspace of its own.
                var checkout = git.GetValueOrDefault(name);
                result[name] = new(local, checkout is null, null, features, normal, dev, checkout);
                continue;
            }

            var checksum = lockFile.Find(name)?.Checksum;
            var directory = store.Fetch(name, version, checksum);
            var package = new LocalPackage(directory, ManifestFiles.Load(directory));
            if (package.Name != name || package.Version != version) {
                throw new RegistryException($"the archive of {name} {version} holds {package.Name} {package.Version}");
            }

            result[name] = new(package, false, checksum, features, normal, dev);
        }

        return result;
    }
}
