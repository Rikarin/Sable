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

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Os, string Arch), Lazy<IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>>>> Parsed = new();

    /// <summary>The modules parsed for the machine running the compiler.</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> Modules => ModulesFor(ParseOptions.Default);

    /// <summary>The modules parsed for the target the options name, once per target: their <c>#if os(...)</c> follows it.</summary>
    static IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> ModulesFor(ParseOptions options) =>
        Parsed.GetOrAdd((options.TargetOs, options.TargetArch), static target => new(() => Load(target.Os, target.Arch))).Value;

    /// <summary>The library modules there are, by name.</summary>
    public static IReadOnlyCollection<string> ModuleNames => [..Modules.Keys];

    /// <summary>A library module's source, every file of it in order: what a cache of anything built from it hashes.</summary>
    public static string Source(string module) =>
        Modules.TryGetValue(module, out var files) ? string.Join("\n", files.Select(static f => f.Text.Text)) : "";

    static IReadOnlyDictionary<string, IReadOnlyList<SyntaxTree>> Load(string os, string arch) {
        var modules = new Dictionary<string, List<SyntaxTree>>(StringComparer.Ordinal);
        var assembly = typeof(Library).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(static n => n.StartsWith("lib/", StringComparison.Ordinal)).Order(StringComparer.Ordinal)) {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            // The standard library is a workspace of its own: what it marks
            // 'package' is shared between its modules, not with programs.
            var tree = SyntaxTree.Parse(new SourceText(reader.ReadToEnd(), resource), ParseOptions.Default.ForTarget(os, arch) with { Workspace = "sable" });
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
    public static IReadOnlyList<SyntaxTree> For(SyntaxTree program) => For([program]);

    /// <summary>The library's trees a program of many files needs, parsed for the target its files are.</summary>
    public static IReadOnlyList<SyntaxTree> For(IReadOnlyList<SyntaxTree> program) {
        var modules = program.Count > 0 ? ModulesFor(program[0].Options) : Modules;
        var needed = new List<SyntaxTree>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { Core };
        var pending = new Queue<string>(program.SelectMany(ImportsOf));
        while (pending.TryDequeue(out var module)) {
            if (!seen.Add(module) || !modules.TryGetValue(module, out var files)) {
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
