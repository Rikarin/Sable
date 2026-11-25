using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Sable.Driver.Build;
using Sable.Driver.Generators;
using Sable.LanguageServer;
using Sable.Syntax;

namespace Sable.Driver.Packages;

/// <summary>
///     What the generators wrote for a target (docs/08): each file under
///     <c>target/generated/</c>, by the file whose declaration it was written
///     for, and what they reported.
/// </summary>
public sealed class GeneratedSources {
    public static readonly GeneratedSources None = new(new Dictionary<string, List<string>>(), [], []);

    readonly IReadOnlyDictionary<string, List<string>> _byOrigin;

    internal GeneratedSources(IReadOnlyDictionary<string, List<string>> byOrigin, IReadOnlyList<string> errors, IReadOnlyList<string> warnings) {
        _byOrigin = byOrigin;
        Errors = errors;
        Warnings = warnings;
    }

    /// <summary>The generators' errors, each formatted against its file.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>The generators' warnings, each formatted against its file.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The files written for the declarations in <paramref name="origin" />, in the order they were written.</summary>
    public IReadOnlyList<string> For(string origin) =>
        _byOrigin.TryGetValue(Path.GetFullPath(origin), out var files) ? files : [];
}

/// <summary>
///     Runs the generators a package can see over its files (docs/08 §8.2),
///     and writes what they produce under <c>target/generated/</c>, where
///     the build compiles it from.
/// </summary>
public static class PackageGenerators {
    /// <summary>
    ///     The generators' output for a member's target and every package it
    ///     compiles with: with <paramref name="dev" />, its tests and benches
    ///     and its dev-dependencies' generators too.
    /// </summary>
    public static GeneratedSources Run(PackageGraph graph, string member, bool dev, bool optimise, BuildTimings? timings = null) {
        using var _ = BuildTimings.Of(timings, "generate");
        var engine = new Engine(graph, member, dev, package => {
            var files = new List<string>(package.Package.LibrarySources());
            if (package.Name == member) {
                files.AddRange(package.Package.Bins.Select(static b => b.Path));
                if (dev) {
                    files.AddRange(PackageBuilder.TestFiles(package));
                }
            }

            var library = package.Package.LibrarySources().ToHashSet(StringComparer.Ordinal);
            return [..files.Distinct(StringComparer.Ordinal).Select(f => new PackageFile(PackageBuilder.Parse(graph, package, f, optimise), f, library.Contains(f)))];
        }, package => {
            IEnumerable<string> files = package.Package.LibrarySources();
            if (package.Name == member) {
                files = files.Concat(package.Package.Bins.Select(static b => b.Path)).Concat(dev ? PackageBuilder.TestFiles(package) : []);
            }

            return files.Any(static f => File.Exists(f) && LibraryGenerators.MayImport(File.ReadAllText(f)));
        });
        if (!engine.HasGenerators) {
            return GeneratedSources.None;
        }

        var byOrigin = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var package in graph.Closure(member, dev)) {
            var outcome = engine.Ensure(package.Name);
            errors.AddRange(outcome.Problems);
            foreach (var (diagnostic, tree) in outcome.Result.Diagnostics) {
                (diagnostic.Severity == DiagnosticSeverity.Error ? errors : warnings).Add(diagnostic.Format(tree.Text));
            }

            if (outcome.Visible == 0) {
                continue;
            }

            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (file, path) in outcome.Written) {
                written.Add(path);
                var origin = Path.GetFullPath(outcome.Paths[file.Origin]);
                if (!byOrigin.TryGetValue(origin, out var list)) {
                    byOrigin[origin] = list = [];
                }

                list.Add(path);
            }

            RemoveStale(GeneratedDirectory(graph.Workspace, package.Name), written);
        }

        return new(byOrigin, [..errors.Distinct()], [..warnings.Distinct()]);
    }

    /// <summary>
    ///     What the language server compiles with a member's documents (docs/08
    ///     §8.7): its generators' output for the documents as they are now, and
    ///     its dependencies' generators' output for their files as the editor
    ///     has them. Written to disk too, so that going to a generated member
    ///     opens a file.
    /// </summary>
    public static GeneratedTrees ForEditor(PackageGraph graph, string member, IReadOnlyList<SyntaxTree> documents, IReadOnlyList<SyntaxTree> dependencies) {
        // A dependency's tree is the file of the package whose directory holds it.
        var directories = graph.Closure(member, dev: true)
            .Select(static p => (Prefix: p.Package.Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, p.Name))
            .OrderByDescending(static p => p.Prefix.Length)
            .ToList();
        var byPackage = new Dictionary<string, List<SyntaxTree>>(StringComparer.Ordinal);
        foreach (var tree in dependencies) {
            var path = Path.GetFullPath(tree.Text.FileName);
            if (directories.FirstOrDefault(d => path.StartsWith(d.Prefix, StringComparison.Ordinal)).Name is { } name && name != member) {
                (byPackage.TryGetValue(name, out var list) ? list : byPackage[name] = []).Add(tree);
            }
        }

        var engine = new Engine(graph, member, dev: true, package => package.Name == member
            ? [..documents.Select(static d => new PackageFile(d, d.Text.FileName, true))]
            : [..(byPackage.TryGetValue(package.Name, out var trees) ? trees : []).Select(static t => new PackageFile(t, t.Text.FileName, true))],
            package => (package.Name == member ? documents : byPackage.TryGetValue(package.Name, out var files) ? files : []).Any(static t => LibraryGenerators.MayImport(t.Text.Text)));
        if (!engine.HasGenerators) {
            return GeneratedTrees.None;
        }

        var generated = new List<SyntaxTree>();
        var diagnostics = new List<(Diagnostic, SyntaxTree)>();
        foreach (var package in graph.Closure(member, dev: true)) {
            var outcome = engine.Ensure(package.Name);
            generated.AddRange(outcome.Trees);
            if (package.Name == member) {
                diagnostics.AddRange(outcome.Result.Diagnostics);
            }
        }

        return new(generated, diagnostics);
    }

    /// <summary>
    ///     What the language server compiles with a generator snapshot case's
    ///     files (docs/08 §8.8): what the package's own generators write for
    ///     them, under <c>target/generated/&lt;package&gt;/tests/&lt;case&gt;/</c>.
    /// </summary>
    public static GeneratedTrees ForTestCase(GeneratorSet? set, string package, string caseName, IReadOnlyList<SyntaxTree> documents, Workspace workspace) {
        if (set is null) {
            return GeneratedTrees.None;
        }

        var result = SourceGenerators.Run(new GenerationInput(package, [set], documents));
        var trees = new List<SyntaxTree>();
        foreach (var file in result.Files) {
            var path = Path.Combine(GeneratedDirectory(workspace, package), "tests", caseName, file.Generator, file.FileName);
            Write(path, file.Text);
            trees.Add(SyntaxTree.Parse(new SourceText(file.Text, path), file.Origin.Options));
        }

        return new(trees, result.Diagnostics);
    }

    /// <summary>A package's file the generators run over: its tree, its path, and whether it is a library file, which its dependents compile.</summary>
    sealed record PackageFile(SyntaxTree Tree, string Path, bool IsLibrary);

    /// <summary>What running one package's generators gave.</summary>
    sealed record PackageOutcome(
        int Visible,
        GenerationResult Result,
        IReadOnlyList<(GeneratedFile File, string Path)> Written,
        IReadOnlyList<SyntaxTree> Trees,
        IReadOnlyList<SyntaxTree> Library,
        IReadOnlyDictionary<SyntaxTree, string> Paths,
        IReadOnlyList<string> Problems);

    /// <summary>
    ///     Runs each package's generators after its dependencies', so that its
    ///     types resolve against their files and what their generators wrote
    ///     (docs/08 §8.3).
    /// </summary>
    sealed class Engine {
        readonly PackageGraph _graph;
        readonly string _member;
        readonly bool _dev;
        readonly Func<ResolvedPackage, IReadOnlyList<PackageFile>> _files;
        readonly Dictionary<string, GeneratorSet?> _sets = new(StringComparer.Ordinal);
        readonly Dictionary<string, PackageOutcome> _done = new(StringComparer.Ordinal);

        /// <param name="importsLibrary">Whether a package may import a library module with generators of its own.</param>
        public Engine(PackageGraph graph, string member, bool dev, Func<ResolvedPackage, IReadOnlyList<PackageFile>> files, Func<ResolvedPackage, bool> importsLibrary) {
            _graph = graph;
            _member = member;
            _dev = dev;
            _files = files;
            HasGenerators = graph.Closure(member, dev).Any(p => SetOf(p.Name) is not null || importsLibrary(p));
        }

        /// <summary>Whether any package the member compiles with has generators: if none does, there is nothing to run.</summary>
        public bool HasGenerators { get; }

        GeneratorSet? SetOf(string name) {
            if (!_sets.TryGetValue(name, out var set)) {
                var package = _graph.Packages[name];
                _sets[name] = set = GeneratorSet.Load(name, package.Package.Directory, file => FileName(_graph, package, file));
            }

            return set;
        }

        public PackageOutcome Ensure(string name) {
            if (_done.TryGetValue(name, out var known)) {
                return known;
            }

            var package = _graph.Packages[name];
            var own = name == _member;
            var references = new List<SyntaxTree>();
            var dependencies = new List<(string, IReadOnlyList<SyntaxTree>)>();
            foreach (var dependency in _graph.Closure(name, own && _dev).Where(p => p.Name != name)) {
                var done = Ensure(dependency.Name);
                references.AddRange(done.Library);
                // Its own files, not what was written for them.
                dependencies.Add((dependency.Name, [..done.Library.Where(done.Paths.ContainsKey)]));
            }

            var files = _files(package);
            var paths = files.ToDictionary(static f => f.Tree, static f => f.Path);
            IEnumerable<string> names = [name, ..package.Dependencies, ..own && _dev ? package.DevDependencies : []];
            var visible = names.Distinct(StringComparer.Ordinal).Select(SetOf).OfType<GeneratorSet>()
                .Concat(LibraryGenerators.VisibleTo(files.Select(static f => f.Tree))).ToList();
            var problems = new List<string>();
            var result = GenerationResult.Empty;
            if (visible.Count > 0) {
                result = SourceGenerators.Run(new GenerationInput(name, visible, [..files.Select(static f => f.Tree)]) {
                    References = references,
                    Dependencies = dependencies,
                    Inputs = Inputs(package, problems),
                });
            }

            var written = new List<(GeneratedFile, string)>();
            var trees = new List<SyntaxTree>();
            var library = files.Where(static f => f.IsLibrary).Select(static f => f.Tree).ToList();
            var libraryTrees = library.ToHashSet();
            foreach (var file in result.Files) {
                var path = Path.Combine(GeneratedDirectory(_graph.Workspace, name), file.Generator, file.FileName);
                Write(path, file.Text);
                written.Add((file, path));
                var tree = SyntaxTree.Parse(new SourceText(file.Text, path), file.Origin.Options);
                trees.Add(tree);
                if (libraryTrees.Contains(file.Origin)) {
                    library.Add(tree);
                }
            }

            return _done[name] = new(visible.Count, result, written, trees, library, paths, problems);
        }

        /// <summary>The files the package's manifest lists for its generators; a listed path that is not there is a problem.</summary>
        static IReadOnlyList<GeneratorInputFile> Inputs(ResolvedPackage package, List<string> problems) {
            var inputs = new List<GeneratorInputFile>();
            foreach (var entry in package.Package.Manifest.GeneratorInputs) {
                var path = Path.GetFullPath(Path.Combine(package.Package.Directory, entry));
                IEnumerable<string> files = Directory.Exists(path) ? Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                    : File.Exists(path) ? [path]
                    : [];
                if (!files.Any() && !Directory.Exists(path)) {
                    problems.Add($"error: {package.Name}: the generator input '{entry}' that its manifest lists does not exist (docs/08 §8.3)");
                    continue;
                }

                inputs.AddRange(files.Select(f => new GeneratorInputFile(
                    Path.GetRelativePath(package.Package.Directory, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(f))));
            }

            return inputs;
        }
    }

    static readonly ConditionalWeakTable<PackageGraph, ConcurrentDictionary<string, GeneratorAttributes>> Attributes = new();

    /// <summary>
    ///     The attributes a package's files may write for its generators and
    ///     its dependencies' and dev-dependencies' (docs/08 §8.2), which the
    ///     compiler accepts as it does its own.
    /// </summary>
    public static GeneratorAttributes AttributesFor(PackageGraph graph, ResolvedPackage package) =>
        Attributes.GetOrCreateValue(graph).GetOrAdd(package.Name, _ => {
            IEnumerable<string> names = [package.Name, ..package.Dependencies, ..package.DevDependencies];
            var sets = names.Distinct(StringComparer.Ordinal)
                .Where(graph.Packages.ContainsKey)
                .Select(name => GeneratorSet.Load(name, graph.Packages[name].Package.Directory, static f => f))
                .OfType<GeneratorSet>()
                .ToList();
            return sets.Count == 0
                ? GeneratorAttributes.None
                : new([..sets.SelectMany(static s => s.Attributes.Types).Distinct().Order(StringComparer.Ordinal)],
                    [..sets.SelectMany(static s => s.Attributes.Members).Distinct().Order(StringComparer.Ordinal)]);
        });

    /// <summary>Where a package's generated files go: <c>target/generated/&lt;package&gt;/</c>.</summary>
    public static string GeneratedDirectory(Workspace workspace, string package) =>
        Path.Combine(workspace.TargetDirectory, "generated", package);

    /// <summary>A generator file's name in diagnostics, as the package's other files are named.</summary>
    static string FileName(PackageGraph graph, ResolvedPackage package, string file) =>
        package.IsLocal
            ? Path.GetRelativePath(graph.Workspace.Root, file).Replace(Path.DirectorySeparatorChar, '/')
            : $"{package.Name}-{package.Version}/{Path.GetRelativePath(package.Package.Directory, file).Replace(Path.DirectorySeparatorChar, '/')}";

    /// <summary>Writes the file only when its text changed, so that what reads its time sees a change only when there is one.</summary>
    static void Write(string path, string text) {
        if (File.Exists(path) && File.ReadAllText(path) == text) {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>
    ///     Takes away what the generators wrote before and did not write
    ///     this time: a declaration that lost its attribute, or a type renamed.
    /// </summary>
    static void RemoveStale(string directory, HashSet<string> written) {
        if (!Directory.Exists(directory)) {
            return;
        }

        foreach (var file in Directory.GetFiles(directory, "*.sb", SearchOption.AllDirectories)) {
            if (!written.Contains(file)) {
                File.Delete(file);
            }
        }
    }
}
