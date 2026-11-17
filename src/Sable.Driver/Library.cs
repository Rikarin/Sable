using System.Reflection;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>
///     The standard library (docs/04 §4.3): Sable source in <c>lib/</c>,
///     embedded in the compiler. <c>Sable.Core</c>'s files are part of every
///     compilation, and come with Sable.Symbols; any other module is part of
///     a program that imports it, with whatever that module imports in turn.
/// </summary>
public static class Library {
    public const string Core = CoreLibrary.ModuleName;

    static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>>> Modules = new(Load);

    /// <summary>The library modules there are, by name.</summary>
    public static IReadOnlyCollection<string> ModuleNames => [..Modules.Value.Keys];

    static IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> Load() {
        var modules = new Dictionary<string, List<SyntaxTree>>(StringComparer.Ordinal);
        var assembly = typeof(Library).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(static n => n.StartsWith("lib/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var tree = SyntaxTree.Parse(new SourceText(reader.ReadToEnd(), resource));
            if (tree.Root is not CompilationUnitSyntax { Module: { } declaration }) {
                continue;
            }

            var name = NameOf(declaration.Name);
            if (!modules.TryGetValue(name, out var files)) {
                modules[name] = files = [];
            }

            files.Add(tree);
        }

        return modules.ToDictionary(static m => m.Key, static m => (IReadOnlyList<SyntaxTree>)m.Value, StringComparer.Ordinal);
    }

    /// <summary>
    ///     The library's trees a program needs: every module it imports,
    ///     transitively. The core is not among them: every compilation has it
    ///     already (<see cref="CoreLibrary" />).
    /// </summary>
    public static IReadOnlyList<SyntaxTree> For(SyntaxTree program) {
        var needed = new List<SyntaxTree>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { Core };
        var pending = new Queue<string>(ImportsOf(program));
        while (pending.TryDequeue(out var module)) {
            if (!seen.Add(module) || !Modules.Value.TryGetValue(module, out var files)) {
                continue;
            }

            foreach (var file in files) {
                needed.Add(file);
                foreach (var imported in ImportsOf(file)) {
                    pending.Enqueue(imported);
                }
            }
        }

        return needed;
    }

    static IEnumerable<string> ImportsOf(SyntaxTree tree) =>
        tree.Root is CompilationUnitSyntax unit ? unit.Imports.Select(static i => NameOf(i.Name)) : [];

    static string NameOf(SyntaxNode name) => name.ToString().Trim();
}
