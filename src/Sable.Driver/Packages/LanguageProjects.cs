using Sable.LanguageServer;

namespace Sable.Driver.Packages;

/// <summary>
///     The packages under an editor's folder, as the language server compiles
///     them (#192): each member of each workspace on its own, parsed with the
///     features it is built with by default, with every file of every package
///     it depends on — path, registry or vendored — as <c>sable check</c>
///     compiles it.
/// </summary>
public static class LanguageProjects {
    /// <summary>Directories never searched for manifests: build output, copies of dependencies, hidden ones.</summary>
    static bool Skipped(string name) => name.StartsWith('.') || name is "bin" or "obj" or "target" or "vendor" or "node_modules";

    /// <summary>
    ///     The packages of every workspace with a manifest under
    ///     <paramref name="root" />, or above it. A workspace that cannot be
    ///     loaded or resolved is reported to <paramref name="log" /> and left
    ///     out: its files are then compiled as loose ones.
    /// </summary>
    public static IReadOnlyList<LanguageProject> For(string root, PackageEnvironment environment, TextWriter? log = null) {
        var workspaces = new Dictionary<string, Workspace>(StringComparer.Ordinal);
        foreach (var directory in ManifestDirectories(root)) {
            try {
                var workspace = Workspace.Load(directory);
                workspaces.TryAdd(workspace.Root, workspace);
            } catch (Exception e) when (e is ManifestException or PackageException or IOException) {
                log?.WriteLine($"sable-lsp: {directory}: {e.Message}");
            }
        }

        var projects = new List<LanguageProject>();
        foreach (var workspace in workspaces.Values.OrderBy(static w => w.Root, StringComparer.Ordinal)) {
            try {
                projects.AddRange(Of(workspace, environment));
            } catch (Exception e) when (e is ManifestException or PackageException or ResolutionException or RegistryException or IOException) {
                log?.WriteLine($"sable-lsp: {workspace.Root}: {e.Message.TrimEnd()}");
            }
        }

        return projects;
    }

    /// <summary>
    ///     Every member of a workspace: its default features, and its
    ///     dependencies' files with theirs — what their generators wrote among
    ///     them. A member's own generators run as it is edited (docs/08 §8.7),
    ///     and its <c>generators/</c> directory is a project of its own: the
    ///     generator program is not part of the package.
    /// </summary>
    static IEnumerable<LanguageProject> Of(Workspace workspace, PackageEnvironment environment) {
        // Resolved as 'sable check' resolves, so the editor and the command agree.
        var graph = PackageGraph.ResolveCached(workspace, environment, new());
        foreach (var member in workspace.Members) {
            var own = graph.Packages[member.Name];
            var generated = PackageGenerators.Run(graph, member.Name, dev: true, optimise: false);
            var attributes = PackageGenerators.AttributesFor(graph, own);
            yield return new(
                member.Name,
                member.Directory,
                [..own.Features.Order(StringComparer.Ordinal)],
                [
                    ..Dependencies(graph, member.Name).SelectMany(p => p.Package.LibrarySources()
                        .SelectMany(f => (IEnumerable<string>)[f, ..generated.For(f)])
                        .Select(f => new LanguageProjectFile(f, [..p.Features.Order(StringComparer.Ordinal)]) {
                            GeneratorAttributes = PackageGenerators.AttributesFor(graph, p).Types,
                            GeneratorMemberAttributes = PackageGenerators.AttributesFor(graph, p).Members,
                        })),
                ]) {
                Generate = trees => PackageGenerators.ForEditor(graph, member.Name, trees),
                GeneratorAttributes = attributes.Types,
                GeneratorMemberAttributes = attributes.Members,
            };

            var generators = Path.Combine(member.Directory, Generators.SourceGenerators.DirectoryName);
            if (Directory.Exists(generators)) {
                yield return new($"{member.Name} generators", generators, [], []);
            }
        }
    }

    /// <summary>
    ///     What a member's files are compiled with: the closure of its
    ///     dependencies, and of its dev-dependencies, since its tests and
    ///     benches are among its files.
    /// </summary>
    static IEnumerable<ResolvedPackage> Dependencies(PackageGraph graph, string member) {
        var seen = new HashSet<string>(StringComparer.Ordinal) { member };
        var result = new List<ResolvedPackage>();
        foreach (var start in graph.Packages[member].Dependencies.Concat(graph.Packages[member].DevDependencies)) {
            foreach (var package in graph.Closure(start)) {
                if (seen.Add(package.Name)) {
                    result.Add(package);
                }
            }
        }

        return result;
    }

    /// <summary>The directories with a manifest at or under the root, and the nearest one above it.</summary>
    static IEnumerable<string> ManifestDirectories(string root) {
        root = Path.GetFullPath(root);
        if (Workspace.FindManifestDirectory(root) is { } enclosing) {
            yield return enclosing;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0) {
            var directory = pending.Pop();
            if (ManifestFiles.Exists(directory)) {
                yield return directory;
            }

            IEnumerable<string> children;
            try {
                children = Directory.EnumerateDirectories(directory);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                continue;
            }

            foreach (var child in children.Order(StringComparer.Ordinal).Reverse()) {
                if (!Skipped(Path.GetFileName(child))) {
                    pending.Push(child);
                }
            }
        }
    }
}
