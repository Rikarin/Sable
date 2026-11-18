namespace Sable.Driver.Packages;

/// <summary>A package on disk: a directory with a manifest (<c>package.sb</c>, or the canonical <c>sable.toml</c>) that describes a package.</summary>
public sealed class LocalPackage {
    public LocalPackage(string directory, Manifest manifest) {
        Directory = Path.GetFullPath(directory);
        Manifest = manifest;
        if (manifest.Package is null) {
            throw new ManifestException([new(manifest.File, 1, "package", "this is a virtual workspace manifest, not a package")]);
        }
    }

    public string Directory { get; }
    public Manifest Manifest { get; }
    public string Name => Manifest.Package!.Name;
    public SemVersion Version => Manifest.Package!.Version;

    public string ManifestPath => ManifestFiles.PathIn(Directory);

    /// <summary>The library's root file: <c>[lib] path</c>, else <c>src/lib.sb</c> when there is one.</summary>
    public string? LibraryFile =>
        Manifest.LibPath is { } path ? Path.GetFullPath(Path.Combine(Directory, path))
        : File.Exists(Path.Combine(Directory, "src", "lib.sb")) ? Path.Combine(Directory, "src", "lib.sb")
        : null;

    /// <summary>
    ///     The binaries: the <c>[[bin]]</c> tables, else <c>src/main.sb</c>
    ///     (named for the package) and each <c>src/bin/*.sb</c>.
    /// </summary>
    public IReadOnlyList<BinTarget> Bins {
        get {
            if (Manifest.Bins.Count > 0) {
                return [..Manifest.Bins.Select(b => b with { Path = Path.GetFullPath(Path.Combine(Directory, b.Path)) })];
            }

            var bins = new List<BinTarget>();
            var main = Path.Combine(Directory, "src", "main.sb");
            if (File.Exists(main)) {
                bins.Add(new(BinaryName(Name), main));
            }

            var binDirectory = Path.Combine(Directory, "src", "bin");
            if (System.IO.Directory.Exists(binDirectory)) {
                bins.AddRange(System.IO.Directory.GetFiles(binDirectory, "*.sb").Order(StringComparer.Ordinal)
                    .Select(static f => new BinTarget(Path.GetFileNameWithoutExtension(f), f)));
            }

            return bins;
        }
    }

    /// <summary>A package name as a file name: <c>org/name</c> becomes <c>name</c>.</summary>
    public static string BinaryName(string package) => package[(package.LastIndexOf('/') + 1)..];

    /// <summary>
    ///     The library's files: every <c>.sb</c> under <c>src/</c> but the
    ///     binaries' entry files and <c>src/bin/</c>, sorted, so a package of
    ///     many files is compiled the same way every time.
    /// </summary>
    public IReadOnlyList<string> LibrarySources() {
        var source = Path.Combine(Directory, "src");
        var files = System.IO.Directory.Exists(source)
            ? System.IO.Directory.GetFiles(source, "*.sb", SearchOption.AllDirectories).Select(Path.GetFullPath).ToList()
            : [];
        if (LibraryFile is { } library && !files.Contains(library) && File.Exists(library)) {
            files.Add(library);
        }

        var bins = Bins.Select(static b => b.Path).ToHashSet(StringComparer.Ordinal);
        var binDirectory = Path.Combine(source, "bin") + Path.DirectorySeparatorChar;
        return [..files.Where(f => !bins.Contains(f) && !f.StartsWith(binDirectory, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }

    public override string ToString() => $"{Name} {Version}";
}

/// <summary>
///     What a command works on: the package in a directory, or the workspace
///     it belongs to (docs/05 §5.2 <c>[workspace]</c>). Members share one
///     <c>sable.lock</c> and one <c>target/</c>, at the workspace's root.
/// </summary>
public sealed class Workspace {
    /// <summary>The manifest a person writes (docs/05 §5.2); <see cref="ManifestFiles" /> finds either kind.</summary>
    public const string ManifestName = ManifestFiles.PackageFile;
    public const string LockName = "sable.lock";

    Workspace(string root, Manifest manifest, IReadOnlyList<LocalPackage> members, LocalPackage? current) {
        Root = root;
        RootManifest = manifest;
        Members = members;
        Current = current;
    }

    /// <summary>The directory of the root manifest.</summary>
    public string Root { get; }

    public Manifest RootManifest { get; }

    /// <summary>The packages of the workspace; for a lone package, just it.</summary>
    public IReadOnlyList<LocalPackage> Members { get; }

    /// <summary>The member the command was run in, if any: what <c>build</c> builds by default.</summary>
    public LocalPackage? Current { get; }

    public bool IsWorkspace => RootManifest.Workspace is not null;

    public string LockPath => Path.Combine(Root, LockName);

    public string TargetDirectory => Path.Combine(Root, "target");

    public string VendorDirectory => Path.Combine(Root, "vendor");

    /// <summary>The nearest directory, from <paramref name="start" /> up, with a manifest.</summary>
    public static string? FindManifestDirectory(string start) {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent) {
            if (ManifestFiles.Exists(directory.FullName)) {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>
    ///     The workspace <paramref name="start" /> is in: the nearest manifest,
    ///     or the workspace above it that lists it as a member.
    /// </summary>
    /// <exception cref="ManifestException">A manifest is not valid.</exception>
    /// <exception cref="PackageException">There is no manifest, or a workspace does not hold together.</exception>
    public static Workspace Load(string start) {
        var nearest = FindManifestDirectory(start)
            ?? throw new PackageException($"could not find {ManifestFiles.PackageFile} in '{Path.GetFullPath(start)}' or any directory above it; 'sable new' or 'sable init' makes one");

        var root = nearest;
        for (var directory = Directory.GetParent(nearest); directory is not null; directory = directory.Parent) {
            if (!ManifestFiles.Exists(directory.FullName)) {
                continue;
            }

            var manifest = ManifestFiles.Load(directory.FullName);
            if (manifest.Workspace is not null && MemberDirectories(directory.FullName, manifest.Workspace).Contains(nearest)) {
                root = directory.FullName;
                break;
            }
        }

        return Open(root, nearest);
    }

    static Workspace Open(string root, string current) {
        var manifest = ManifestFiles.Load(root);
        var members = new List<LocalPackage>();
        if (manifest.Package is not null) {
            members.Add(new(root, manifest));
        }

        if (manifest.Workspace is { } workspace) {
            foreach (var directory in MemberDirectories(root, workspace)) {
                if (directory == root) {
                    continue;
                }

                var member = ManifestFiles.Load(directory);
                if (member.Package is null) {
                    throw new PackageException($"{ManifestFiles.PathIn(directory)}: a workspace member needs a package manifest");
                }

                if (member.Workspace is not null) {
                    throw new PackageException($"{ManifestFiles.PathIn(directory)}: a workspace member cannot be a workspace itself");
                }

                members.Add(new(directory, member));
            }
        }

        var duplicate = members.GroupBy(static m => m.Name).FirstOrDefault(static g => g.Count() > 1);
        if (duplicate is not null) {
            throw new PackageException($"two workspace members are called '{duplicate.Key}': {string.Join(" and ", duplicate.Select(static m => m.Directory))}");
        }

        return new(root, manifest, members, members.FirstOrDefault(m => m.Directory == current));
    }

    /// <summary>The member directories a <c>[workspace]</c> names: globs with <c>*</c> in any segment, less the exclusions.</summary>
    static HashSet<string> MemberDirectories(string root, WorkspaceSpec workspace) {
        var excluded = workspace.Exclude.SelectMany(e => Expand(root, e)).ToHashSet(StringComparer.Ordinal);
        return [
            ..workspace.Members.SelectMany(m => Expand(root, m))
                .Where(d => !excluded.Contains(d) && !excluded.Any(e => d.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                .Where(static d => ManifestFiles.Exists(d)),
        ];
    }

    static IEnumerable<string> Expand(string root, string pattern) {
        IEnumerable<string> current = [Path.GetFullPath(root)];
        foreach (var segment in pattern.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            current = segment.Contains('*', StringComparison.Ordinal)
                ? current.Where(Directory.Exists).SelectMany(d => Directory.GetDirectories(d, segment)).Order(StringComparer.Ordinal)
                : current.Select(d => Path.GetFullPath(Path.Combine(d, segment)));
        }

        return current;
    }

    /// <summary>
    ///     Every package on disk the workspace uses: its members and,
    ///     transitively, their path dependencies — by name.
    /// </summary>
    public IReadOnlyDictionary<string, LocalPackage> LocalPackages() {
        var locals = new Dictionary<string, LocalPackage>(StringComparer.Ordinal);
        var pending = new Queue<(LocalPackage Package, bool Member)>(Members.Select(static m => (m, true)));
        foreach (var member in Members) {
            locals[member.Name] = member;
        }

        while (pending.TryDequeue(out var item)) {
            var (package, member) = item;
            IEnumerable<DependencySpec> dependencies = member
                ? [..package.Manifest.Dependencies, ..package.Manifest.DevDependencies, ..package.Manifest.BuildDependencies]
                : [..package.Manifest.Dependencies, ..package.Manifest.BuildDependencies];
            foreach (var dependency in dependencies.Where(static d => d.Path is not null)) {
                var directory = Path.GetFullPath(Path.Combine(package.Directory, dependency.Path!));
                if (!ManifestFiles.Exists(directory)) {
                    throw new PackageException($"{package.ManifestPath}:{dependency.Line}: dependency '{dependency.Name}': there is no {ManifestFiles.PackageFile} in '{directory}'");
                }

                if (locals.TryGetValue(dependency.Name, out var known)) {
                    if (known.Directory != directory) {
                        throw new PackageException($"{package.ManifestPath}:{dependency.Line}: dependency '{dependency.Name}': two packages called '{dependency.Name}', at {known.Directory} and {directory}");
                    }

                    continue;
                }

                var local = new LocalPackage(directory, ManifestFiles.Load(directory));
                if (local.Name != dependency.Name) {
                    throw new PackageException($"{package.ManifestPath}:{dependency.Line}: dependency '{dependency.Name}': the package at '{dependency.Path}' is called '{local.Name}'");
                }

                locals[local.Name] = local;
                pending.Enqueue((local, false));
            }
        }

        return locals;
    }

    /// <summary>The member called <paramref name="name" />.</summary>
    public LocalPackage Member(string name) =>
        Members.FirstOrDefault(m => m.Name == name) ?? throw new PackageException($"the workspace has no member called '{name}'");

    /// <summary>A path relative to the workspace root, with '/' between its parts: how the lock file writes one.</summary>
    public string Relative(string path) {
        var relative = Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.Length == 0 ? "." : relative;
    }
}

/// <summary>A package command cannot do what it was asked; the message says why.</summary>
public sealed class PackageException(string message) : Exception(message);
