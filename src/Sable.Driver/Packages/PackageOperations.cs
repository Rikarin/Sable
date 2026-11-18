using System.Diagnostics;
using System.Text;
using Sable.CodeGen;
using Sable.Symbols;

namespace Sable.Driver.Packages;

/// <summary>What <c>sable publish</c> would upload, or did.</summary>
public sealed record PublishPlan(LocalPackage Package, IReadOnlyDictionary<string, byte[]> Files, byte[] Archive, IndexEntry Entry);

/// <summary>The package commands that are not building: add, remove, update, tree, publish, yank, vendor.</summary>
public static class PackageOperations {
    static readonly string[] DependencySections = ["dependencies", "dev-dependencies", "build-dependencies"];

    /// <summary>
    ///     <c>sable add &lt;pkg&gt;[@&lt;req&gt;] [--dev] [--path &lt;dir&gt;]</c>: writes
    ///     the dependency into the manifest, and resolves to bring the lock
    ///     file up to date. Undone when it does not resolve.
    /// </summary>
    public static string Add(Workspace workspace, PackageEnvironment environment, LocalPackage member, string specification, bool dev, string? path) {
        var at = specification.IndexOf('@', 1);
        var name = at < 0 ? specification : specification[..at];
        var requirementText = at < 0 ? null : specification[(at + 1)..];
        if (!Manifest.IsValidPackageName(name)) {
            throw new PackageException($"'{name}' is not a package name");
        }

        VersionReq? requirement = null;
        if (requirementText is not null) {
            requirement = VersionReq.TryParse(requirementText, out var parsed, out var error) ? parsed : throw new PackageException($"'{requirementText}': {error}");
        }

        string value;
        string element;
        if (path is not null) {
            var directory = Path.GetFullPath(Path.Combine(member.Directory, path));
            if (!ManifestFiles.Exists(directory)) {
                throw new PackageException($"there is no {ManifestFiles.PackageFile} in '{directory}'");
            }

            var target = ManifestFiles.Load(directory);
            if (target.Package?.Name != name) {
                throw new PackageException($"the package at '{path}' is called '{target.Package?.Name}', not '{name}'");
            }

            var relative = Path.GetRelativePath(member.Directory, directory).Replace(Path.DirectorySeparatorChar, '/');
            element = SableManifestEditor.Element(name, requirement?.Text, relative);
            value = requirement is null
                ? $"{{ path = {TomlText.Quote(relative)} }}"
                : $"{{ version = {TomlText.Quote(requirement.Text)}, path = {TomlText.Quote(relative)} }}";
        } else {
            var registry = environment.RegistryFor(workspace);
            if (registry.Offline) {
                throw new PackageException($"cannot look {name} up: {registry.WhyUnavailable}; add it with --path, or without --offline");
            }

            var available =registry.Entries(name).Where(static e => !e.Yanked).ToList();
            if (available.Count == 0) {
                throw new PackageException(registry.Entries(name).Count > 0 ? $"every version of {name} is yanked" : $"the registry has no package called '{name}'");
            }

            if (requirement is null) {
                var latest = available.Select(static e => e.Version).Where(static v => !v.IsPrerelease).DefaultIfEmpty(available.Max(static e => e.Version)!).Max()!;
                requirement = VersionReq.Parse(latest.ToString());
            } else if (!available.Any(e => requirement.Matches(e.Version))) {
                throw new PackageException($"no version of {name} matches '{requirement.Text}'; there are {string.Join(", ", available.Select(static e => e.Version))}");
            }

            value = TomlText.Quote(requirement.Text);
            element = SableManifestEditor.Element(name, requirement.Text, null);
        }

        var original = File.ReadAllText(member.ManifestPath);
        if (member.ManifestPath.EndsWith(".sb", StringComparison.Ordinal)) {
            var edited = SableManifestEditor.Set(original, member.ManifestPath, SableManifestEditor.FieldFor(dev), name, element);
            SaveSable(member.ManifestPath, original, edited);
        } else {
            ManifestEditor.SetDependency(member.ManifestPath, dev ? "dev-dependencies" : "dependencies", name, value);
        }
        try {
            PackageGraph.Resolve(Workspace.Load(member.Directory), environment);
        } catch {
            File.WriteAllText(member.ManifestPath, original);
            throw;
        }

        environment.Status.WriteLine($"      Adding {name} {(path is not null ? $"(path {path})" : requirement!.Text)} to {(dev ? "dev-dependencies" : "dependencies")}");
        return value;
    }

    /// <summary><c>sable remove &lt;pkg&gt;</c>: takes the dependency out of the manifest and the lock file.</summary>
    public static void Remove(PackageEnvironment environment, LocalPackage member, string name) {
        if (member.ManifestPath.EndsWith(".sb", StringComparison.Ordinal)) {
            var original = File.ReadAllText(member.ManifestPath);
            var edited = SableManifestEditor.Remove(original, member.ManifestPath, name)
                ?? throw new PackageException($"{member.Name} has no dependency called '{name}'");
            SaveSable(member.ManifestPath, original, edited);
        } else if (!ManifestEditor.RemoveDependency(member.ManifestPath, name, DependencySections)) {
            throw new PackageException($"{member.Name} has no dependency called '{name}'");
        }

        PackageGraph.Resolve(Workspace.Load(member.Directory), environment);
        environment.Status.WriteLine($"    Removing {name} from {member.Name}");
    }

    /// <summary>Writes an edited <c>package.sb</c>, and puts the original back if it no longer evaluates.</summary>
    static void SaveSable(string path, string original, string edited) {
        File.WriteAllText(path, edited);
        try {
            ManifestFiles.Load(Path.GetDirectoryName(path)!);
        } catch (ManifestException) {
            File.WriteAllText(path, original);
            throw;
        }
    }

    /// <summary><c>sable update [pkg]</c>: re-resolves within the manifests' bounds, ignoring what the lock file chose. Returns the changes.</summary>
    public static IReadOnlyList<string> Update(Workspace workspace, PackageEnvironment environment, IReadOnlyList<string> packages) {
        var before = LockFile.Load(workspace.LockPath);
        var graph = PackageGraph.Resolve(workspace, environment, new(Update: packages.ToHashSet(StringComparer.Ordinal)));
        var changes = new List<string>();
        var old = before?.Packages.ToDictionary(static p => p.Name, static p => p.Version, StringComparer.Ordinal) ?? [];
        foreach (var package in graph.Lock.Packages) {
            if (!old.TryGetValue(package.Name, out var version)) {
                changes.Add($"      Adding {package.Name} v{package.Version}");
            } else if (version != package.Version) {
                changes.Add($"    Updating {package.Name} v{version} -> v{package.Version}");
            }
        }

        changes.AddRange(old.Keys.Where(n => graph.Lock.Find(n) is null).Order(StringComparer.Ordinal).Select(n => $"    Removing {n} v{old[n]}"));
        foreach (var change in changes) {
            environment.Error.WriteLine(change);
        }

        return changes;
    }

    /// <summary><c>sable tree</c>: the resolved dependency graph of each selected member, as a tree.</summary>
    public static string Tree(Workspace workspace, PackageEnvironment environment, string? package) {
        var graph = PackageGraph.Resolve(workspace, environment);
        var builder = new StringBuilder();
        foreach (var member in PackageBuilder.Selected(workspace, package)) {
            var root = graph.Packages[member.Name];
            builder.Append(Label(root, workspace, true)).Append('\n');
            var expanded = new HashSet<string>(StringComparer.Ordinal);
            WriteChildren(builder, graph, workspace, root.Dependencies, "", expanded);
            if (root.DevDependencies.Count > 0) {
                builder.Append("[dev-dependencies]\n");
                WriteChildren(builder, graph, workspace, root.DevDependencies, "", expanded);
            }
        }

        return builder.ToString();
    }

    static string Label(ResolvedPackage package, Workspace workspace, bool root) {
        var label = $"{package.Name} v{package.Version}";
        if (package.IsLocal) {
            label += root ? $" ({package.Package.Directory})" : $" (path {workspace.Relative(package.Package.Directory)})";
        } else if (package.Git is { } git) {
            label += $" ({git.Url}#{git.ShortCommit})";
        }

        var features = package.Features.Where(static f => f != "default").Order(StringComparer.Ordinal).ToList();
        return features.Count > 0 ? $"{label} [features: {string.Join(", ", features)}]" : label;
    }

    static void WriteChildren(StringBuilder builder, PackageGraph graph, Workspace workspace, IReadOnlyList<string> children, string prefix, HashSet<string> expanded) {
        for (var i = 0; i < children.Count; i++) {
            var last = i == children.Count - 1;
            var child = graph.Packages[children[i]];
            var repeated = child.Dependencies.Count > 0 && !expanded.Add(child.Name);
            builder.Append(prefix).Append(last ? "└── " : "├── ").Append(Label(child, workspace, false)).Append(repeated ? " (*)" : "").Append('\n');
            if (!repeated) {
                WriteChildren(builder, graph, workspace, child.Dependencies, prefix + (last ? "    " : "│   "), expanded);
            }
        }
    }

    /// <summary>
    ///     What <c>sable publish</c> uploads: the package's files (everything
    ///     but <c>target/</c>, <c>vendor/</c>, hidden files, the lock file and
    ///     nested packages) in a deterministic archive, and its index entry.
    ///     Refuses a package that does not type-check, or whose dependencies
    ///     the registry could not hold.
    /// </summary>
    public static PublishPlan Plan(Workspace workspace, PackageEnvironment environment, LocalPackage package) {
        var manifest = package.Manifest;
        var dependencies = new List<IndexDependency>();
        foreach (var (kind, specs) in new[] { ("normal", manifest.Dependencies), ("dev", manifest.DevDependencies), ("build", manifest.BuildDependencies) }) {
            foreach (var spec in specs) {
                if (spec.Version is null) {
                    // A path-only dev-dependency is dropped, as it means nothing to whoever downloads the package.
                    if (kind == "dev") {
                        continue;
                    }

                    throw spec.Git is { } url
                        ? new PackageException($"{package.ManifestPath}:{spec.Line}: {kind} dependency '{spec.Name}' comes only from git; a published package needs a version requirement for it, which the registry must have, like {{ version = \"1.0\", git = \"{url}\" }}")
                        : new PackageException($"{package.ManifestPath}:{spec.Line}: {kind} dependency '{spec.Name}' has only a path; a published package needs a version requirement for it, like {{ version = \"1.0\", path = \"{spec.Path}\" }}");
                }

                dependencies.Add(new(spec.Name, spec.Version.Text, kind, spec.Optional, spec.DefaultFeatures, spec.Features));
            }
        }

        // It must type-check.
        if (PackageBuilder.Check(workspace, environment, new(Package: package.Name)) != 0) {
            throw new PackageException($"{package.Name} has errors, so it cannot be published");
        }

        var files = Published(package.Directory);
        var archive = Registry.Pack(files);
        var entry = new IndexEntry(package.Name, package.Version, Registry.Checksum(archive), dependencies, manifest.Features, false, "", "", manifest.Package?.Description);
        return new(package, files, archive, entry);
    }

    /// <summary>
    ///     What a package publishes: its files, with a <c>package.sb</c>
    ///     replaced by the canonical <c>sable.toml</c> it evaluates to
    ///     (docs/05 §5.2.6), so that the registry and every consumer read data
    ///     and never run Sable to learn what a package depends on.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> Published(string directory) {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, bytes) in Files(directory)) {
            files[path] = bytes;
        }

        if (files.Remove(ManifestFiles.PackageFile)) {
            files[ManifestFiles.TomlFile] = Encoding.UTF8.GetBytes(ManifestEvaluator.Evaluate(Path.Combine(directory, ManifestFiles.PackageFile)));
        }

        files.Remove(ManifestFiles.WorkspaceFile);
        return files;
    }

    /// <summary>The files a package publishes, by path relative to it with '/'.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files(string directory) {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        void Walk(string current, string relative) {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal)) {
                var name = Path.GetFileName(entry);
                var path = relative.Length == 0 ? name : $"{relative}/{name}";
                if (name.StartsWith('.') || (relative.Length == 0 && name is "target" or "vendor" or Workspace.LockName)) {
                    continue;
                }

                if (Directory.Exists(entry)) {
                    // A nested package is its own package.
                    if (!ManifestFiles.Exists(entry)) {
                        Walk(entry, path);
                    }

                    continue;
                }

                files[path] = File.ReadAllBytes(entry);
            }
        }

        Walk(directory, "");
        return files;
    }

    /// <summary>What a dry run prints: every file and its size, the checksum, and the dependencies recorded.</summary>
    public static string Describe(PublishPlan plan) {
        var builder = new StringBuilder();
        builder.Append($"   Packaging {plan.Package.Name} v{plan.Package.Version} ({plan.Package.Directory})\n");
        foreach (var (path, bytes) in plan.Files) {
            builder.Append($"      {bytes.Length,8} B  {path}\n");
        }

        builder.Append($"    Packaged {plan.Files.Count} file(s), {plan.Archive.Length} B archive, {plan.Entry.Checksum}\n");
        foreach (var dependency in plan.Entry.Dependencies) {
            builder.Append($"  Depends on {dependency.Name} {dependency.Requirement}{(dependency.Kind == "normal" ? "" : $" ({dependency.Kind})")}{(dependency.Optional ? " (optional)" : "")}\n");
        }

        return builder.ToString();
    }

    /// <summary>
    ///     <c>sable publish</c> (docs/05 §5.5): refuses a dirty git tree, a
    ///     commit without a tag for the version, and failing tests, unless
    ///     told otherwise; then uploads the package, signed with the key at
    ///     <see cref="PackageEnvironment.KeyPath" /> (made on first publish).
    ///     With <paramref name="dryRun" />, shows what would be uploaded and
    ///     uploads nothing.
    /// </summary>
    /// <param name="registry">Where to publish; by default the workspace's registry.</param>
    public static PublishPlan Publish(
        Workspace workspace,
        PackageEnvironment environment,
        LocalPackage package,
        bool dryRun,
        bool allowDirty = false,
        bool verify = true,
        bool allowUntagged = false,
        IRegistry? registry = null) {
        if (!allowDirty && DirtyFiles(package.Directory) is { Count: > 0 } dirty) {
            throw new PackageException($"the working tree has uncommitted changes ({string.Join(", ", dirty.Take(5))}{(dirty.Count > 5 ? ", …" : "")}); commit them, or pass --allow-dirty");
        }

        // The version published is a version in the history: a tag on the commit.
        if (!allowUntagged && Tags(package.Directory) is { } tags && !tags.Any(t => VersionTags(package).Contains(t))) {
            throw new PackageException(
                $"the commit being published has no tag for {package.Version} ({(tags.Count == 0 ? "it has none" : $"it has {string.Join(", ", tags)}")}); " +
                $"tag it with 'git tag {VersionTags(package)[0]}', or pass --allow-untagged");
        }

        registry ??= environment.RegistryFor(workspace);
        if (registry.Entry(package.Name, package.Version) is not null) {
            throw new RegistryException($"{package.Name} {package.Version} is already published, and a published version never changes: publish a new version");
        }

        var plan = Plan(workspace, environment, package);
        if (verify) {
            Test(workspace, environment, package);
        }

        environment.Out.Write(Describe(plan));

        if (dryRun) {
            environment.Out.WriteLine("    Dry run: nothing was uploaded");
            return plan;
        }

        using var key = Registry.LoadOrCreateKey(environment.KeyPath, environment.Status);
        var entry = registry.Publish(plan.Entry, plan.Archive, key);
        environment.Status.WriteLine($"   Published {package.Name} v{package.Version} to {registry.Location}");
        return plan with { Entry = entry };
    }

    /// <summary>The tag names that mark a version: <c>v1.2.0</c>, <c>1.2.0</c>, and for a workspace's member <c>name-v1.2.0</c> or <c>name@1.2.0</c>.</summary>
    static IReadOnlyList<string> VersionTags(LocalPackage package) {
        var name = LocalPackage.BinaryName(package.Name);
        return [$"v{package.Version}", $"{package.Version}", $"{name}-v{package.Version}", $"{name}@{package.Version}", $"{package.Name}@{package.Version}"];
    }

    /// <summary>The tags on the commit checked out in <paramref name="directory" />; null when it is not in a git repository.</summary>
    static IReadOnlyList<string>? Tags(string directory) =>
        Git(directory, "rev-parse", "--is-inside-work-tree") is null
            ? null
            : Git(directory, "tag", "--points-at", "HEAD") is { } output
                ? [..output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static t => t.Trim())]
                : [];

    /// <summary>What git prints, or null when it fails or is not there.</summary>
    static string? Git(string directory, params string[] arguments) {
        try {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            foreach (var argument in arguments) {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null) {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Result : null;
        } catch (System.ComponentModel.Win32Exception) {
            return null;
        }
    }

    /// <summary>
    ///     Builds and runs the package's tests, as <c>sable test</c> does;
    ///     refuses the publish when they fail (docs/05 §5.5). A package
    ///     without tests passes.
    /// </summary>
    static void Test(Workspace workspace, PackageEnvironment environment, LocalPackage package) {
        var graph = PackageBuilder.Resolve(workspace, environment, new(Package: package.Name));
        var result = TestHarness.Compile(PackageBuilder.TestProgram(graph, package.Name), TestKind.Test, out var tests);
        if (!result.Succeeded) {
            throw new PackageException($"the tests of {package.Name} do not compile, so it is not published:\n\n{string.Join("\n\n", result.Errors)}");
        }

        if (tests.Count == 0) {
            return;
        }

        if (!NativeBuild.IsAvailable()) {
            throw new PackageException($"cannot run the tests of {package.Name}: there is no '{NativeBuild.CompilerPath}' to build them; set CC, or pass --no-verify");
        }

        var directory = Directory.CreateTempSubdirectory("sable-publish-");
        try {
            var executable = Path.Combine(directory.FullName, "tests");
            var build = NativeBuild.Build(result.LlvmIr!, executable, NativeOptions.Of(false, false) with { LinkArguments = graph.LinkArguments(package.Name) });
            if (!build.Succeeded) {
                throw new PackageException($"internal error: the tests of {package.Name} did not build:\n{build.Output}");
            }

            environment.Status.WriteLine($"     Testing {package.Name} v{package.Version}: {tests.Count} test(s)");
            // A Sable program writes UTF-8, whatever the console's code page.
            var start = new ProcessStartInfo(executable) {
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using var process = Process.Start(start) ?? throw new PackageException($"could not run the tests of {package.Name}");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) {
                throw new PackageException($"the tests of {package.Name} fail, so it is not published (--no-verify skips them):\n{output.Result}{errors}".TrimEnd());
            }
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>Files git reports as changed under <paramref name="directory" />; none when git is not there or this is not a repository.</summary>
    static IReadOnlyList<string> DirtyFiles(string directory) {
        try {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            foreach (var argument in new[] { "status", "--porcelain", "--", "." }) {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null) {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode != 0 ? [] : [..output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static l => l[3..].Trim())];
        } catch (System.ComponentModel.Win32Exception) {
            return [];
        }
    }

    /// <summary><c>sable yank &lt;pkg&gt;@&lt;version&gt; [--undo]</c>.</summary>
    public static void Yank(Workspace? workspace, PackageEnvironment environment, string name, SemVersion version, bool undo, IRegistry? registry = null) {
        registry ??= environment.RegistryFor(workspace);
        if (!File.Exists(environment.KeyPath)) {
            throw new PackageException($"there is no publishing key at {environment.KeyPath}; only the package's owner may yank it");
        }

        using var key = Registry.LoadOrCreateKey(environment.KeyPath);
        registry.Yank(name, version, key, !undo);
        environment.Status.WriteLine($"{(undo ? "   Unyanked" : "      Yanked")} {name} v{version}");
    }

    /// <summary>
    ///     <c>sable vendor</c>: copies every registry package the lock file
    ///     holds into <c>vendor/</c>, where builds find them before the cache
    ///     or the registry — so a build needs neither.
    /// </summary>
    public static IReadOnlyList<string> Vendor(Workspace workspace, PackageEnvironment environment) {
        var graph = PackageGraph.Resolve(workspace, environment, new(Features: new([], AllFeatures: true)));
        var vendored = new List<string>();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var locked in graph.Lock.Packages.Where(static p => p.IsRegistry)) {
            var source = graph.Fetch(locked);
            var target = Path.Combine(workspace.VendorDirectory, Path.GetFileName(source));
            keep.Add(Path.GetFileName(target));
            if (Path.GetFullPath(source) != Path.GetFullPath(target)) {
                if (Directory.Exists(target)) {
                    Directory.Delete(target, true);
                }

                Copy(source, target);
            }

            vendored.Add($"{locked.Name} v{locked.Version}");
            environment.Status.WriteLine($"   Vendoring {locked.Name} v{locked.Version} to {workspace.Relative(target)}");
        }

        if (Directory.Exists(workspace.VendorDirectory)) {
            foreach (var stale in Directory.GetDirectories(workspace.VendorDirectory).Where(d => !keep.Contains(Path.GetFileName(d)))) {
                Directory.Delete(stale, true);
            }
        }

        return vendored;
    }

    static void Copy(string source, string target) {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.GetDirectories(source)) {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
