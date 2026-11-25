using Sable.Driver.Build;
using Sable.LanguageServer;
using Sable.Syntax;

namespace Sable.Driver.Generators;

/// <summary>
///     The standard library's own source generators (docs/08 §8.1): the files
///     under <c>lib/&lt;Module&gt;/generators/</c>, embedded in the compiler,
///     which run for every package with a file that imports the module, and
///     for files in no package.
/// </summary>
public static class LibraryGenerators {
    static readonly Lazy<IReadOnlyList<(string Module, GeneratorSet Set)>> Loaded = new(static () =>
        [..Library.Generators.OrderBy(static g => g.Key, StringComparer.Ordinal).Select(static g => (g.Key, GeneratorSet.OfLibrary(g.Key, g.Value)))]);

    /// <summary>The generators, by the module they are part of.</summary>
    public static IReadOnlyList<(string Module, GeneratorSet Set)> All => Loaded.Value;

    /// <summary>The generators the files can see: those of the modules they import.</summary>
    public static IReadOnlyList<GeneratorSet> VisibleTo(IEnumerable<SyntaxTree> files) {
        var imported = files.SelectMany(Imports).ToHashSet(StringComparer.Ordinal);
        return [..All.Where(g => imported.Contains(g.Module)).Select(static g => g.Set)];
    }

    /// <summary>Whether a file's text may import a module with generators: what is asked before anything is parsed.</summary>
    public static bool MayImport(string text) => All.Any(g => text.Contains(g.Module, StringComparison.Ordinal));

    static IEnumerable<string> Imports(SyntaxTree tree) =>
        MayImport(tree.Text.Text) && tree.Root is CompilationUnitSyntax unit ? unit.Imports.Select(static i => i.Name.ToString().Trim()) : [];

    /// <summary>The program, with what the library's generators write for its files that are in no package; and what they reported, formatted.</summary>
    public static (IReadOnlyList<SyntaxTree> Program, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) Expand(IReadOnlyList<SyntaxTree> program) {
        var (files, trees, diagnostics) = Run(program);
        if (files.Count == 0 && diagnostics.Count == 0) {
            return (program, [], []);
        }

        string[] Of(DiagnosticSeverity severity) => [..diagnostics.Where(d => d.Diagnostic.Severity == severity).Select(static d => d.Diagnostic.Format(d.Tree.Text)).Distinct()];
        return ([..program, ..trees], Of(DiagnosticSeverity.Error), Of(DiagnosticSeverity.Warning));
    }

    /// <summary>The same for the editor's files that are in no package.</summary>
    public static GeneratedTrees ForEditor(IReadOnlyList<SyntaxTree> program) {
        var (_, trees, diagnostics) = Run(program);
        return new(trees, diagnostics);
    }

    /// <summary>What the library's generators write for a program's files that are in no package.</summary>
    public static (IReadOnlyList<GeneratedFile> Files, IReadOnlyList<SyntaxTree> Trees, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree Tree)> Diagnostics) Run(IReadOnlyList<SyntaxTree> program) {
        var loose = program.Where(static t => t.Options.Package is null).ToList();
        if (loose.Count == 0 || loose.Exists(static t => IsGenerated(t.Text.FileName)) || VisibleTo(loose) is not { Count: > 0 } visible) {
            return ([], [], []);
        }

        // Files of different modules are one program, and one package here:
        // a doc example's file has the name and module of another.
        var result = SourceGenerators.Run(new GenerationInput("", visible, loose));
        return (result.Files, [..result.Files.Select(static f => SyntaxTree.Parse(new SourceText(f.Text, NameOf(f)), f.Origin.Options))], result.Diagnostics);
    }

    const string Prefix = "generated/";

    /// <summary>What diagnostics call a file written for one in no package.</summary>
    public static string NameOf(GeneratedFile file) => $"{Prefix}{file.Generator}/{file.FileName}";

    static bool IsGenerated(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);
}
