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
        var sets = new Dictionary<string, GeneratorSet?>(StringComparer.Ordinal);

        GeneratorSet? SetOf(string name) {
            if (!sets.TryGetValue(name, out var set)) {
                var package = graph.Packages[name];
                sets[name] = set = GeneratorSet.Load(name, package.Package.Directory, file => DisplayName(graph, package, file));
            }

            return set;
        }

        var byOrigin = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var package in graph.Closure(member, dev)) {
            var own = package.Name == member;
            IEnumerable<string> names = [package.Name, ..package.Dependencies, ..own && dev ? package.DevDependencies : []];
            var visible = names.Distinct(StringComparer.Ordinal).Select(SetOf).OfType<GeneratorSet>().ToList();
            if (visible.Count == 0) {
                continue;
            }

            var files = new List<string>(package.Package.LibrarySources());
            if (own) {
                files.AddRange(package.Package.Bins.Select(static b => b.Path));
                if (dev) {
                    files.AddRange(PackageBuilder.TestFiles(package));
                }
            }

            var paths = new Dictionary<SyntaxTree, string>();
            var trees = new List<SyntaxTree>();
            foreach (var file in files.Distinct(StringComparer.Ordinal)) {
                var tree = PackageBuilder.Parse(graph, package, file, optimise);
                paths[tree] = file;
                trees.Add(tree);
            }

            var result = SourceGenerators.Run(package.Name, visible, trees);
            foreach (var (diagnostic, tree) in result.Diagnostics) {
                (diagnostic.Severity == DiagnosticSeverity.Error ? errors : warnings).Add(diagnostic.Format(tree.Text));
            }

            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in result.Files) {
                var path = Path.Combine(GeneratedDirectory(graph.Workspace, package.Name), file.Generator, file.FileName);
                Write(path, file.Text);
                written.Add(path);
                var origin = Path.GetFullPath(paths[file.Origin]);
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
    ///     What the language server compiles with a member's documents: its
    ///     generators' output for the documents as they are now, written to
    ///     disk too, so that going to a generated member opens a file.
    /// </summary>
    public static GeneratedTrees ForEditor(PackageGraph graph, string member, IReadOnlyList<SyntaxTree> documents) {
        var package = graph.Packages[member];
        IEnumerable<string> names = [member, ..package.Dependencies, ..package.DevDependencies];
        var visible = names.Distinct(StringComparer.Ordinal)
            .Select(name => GeneratorSet.Load(name, graph.Packages[name].Package.Directory, static f => f))
            .OfType<GeneratorSet>()
            .ToList();
        if (visible.Count == 0) {
            return GeneratedTrees.None;
        }

        var result = SourceGenerators.Run(member, visible, documents);
        var trees = new List<SyntaxTree>();
        foreach (var file in result.Files) {
            var path = Path.Combine(GeneratedDirectory(graph.Workspace, member), file.Generator, file.FileName);
            Write(path, file.Text);
            trees.Add(SyntaxTree.Parse(new SourceText(file.Text, path), file.Origin.Options));
        }

        return new(trees, result.Diagnostics);
    }

    /// <summary>Where a package's generated files go: <c>target/generated/&lt;package&gt;/</c>.</summary>
    public static string GeneratedDirectory(Workspace workspace, string package) =>
        Path.Combine(workspace.TargetDirectory, "generated", package);

    /// <summary>A generator file's name in diagnostics, as the package's other files are named.</summary>
    static string DisplayName(PackageGraph graph, ResolvedPackage package, string file) =>
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
