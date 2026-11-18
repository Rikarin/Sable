using Sable.Driver.Docs;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable doc</c>: documentation from <c>///</c> comments for the public
///     declarations of a program's or a library's modules (docs/05 §5.1), as
///     static HTML, or with <c>--json</c> as a symbol graph.
/// </summary>
public sealed class DocCommand : Command {
    public override string Name => "doc";
    public override string Summary => "generate documentation, or a symbol graph";

    public override string Usage => """
        usage: sable doc [paths...] [--out <dir>] [--json]
               sable doc --std [--out <dir>] [--json]

          paths      .sb files, or directories of them (default: the current directory);
                     the modules they declare are documented
          --std      document the standard library instead
          --out dir  where to write it (default: target/doc)
          --json     write the symbol graph, symbols.json, instead of HTML
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var paths = new List<string>();
        var output = Path.Combine("target", "doc");
        var json = false;
        var standardLibrary = false;
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--out" when i + 1 < arguments.Count: output = arguments[++i]; break;
                case "--json": json = true; break;
                case "--std": standardLibrary = true; break;
                default:
                    if (arguments[i].StartsWith('-')) {
                        return BadUsage();
                    }

                    paths.Add(arguments[i]);
                    break;
            }
        }

        if (standardLibrary && paths.Count > 0) {
            return BadUsage();
        }

        DocSet set;
        if (standardLibrary) {
            set = StandardLibrary();
        } else {
            var files = FmtCommand.SourceFiles(paths.Count == 0 ? ["."] : paths).ToList();
            if (files.Count == 0) {
                Console.Error.WriteLine("sable doc: no .sb files to document");
                return 1;
            }

            var trees = files.Select(static f => SyntaxTree.Parse(new SourceText(File.ReadAllText(f), f))).ToList();
            if (ForSources(trees, out var errors) is not { } built) {
                foreach (var error in errors) {
                    Console.Error.WriteLine(error);
                }

                return 1;
            }

            set = built;
        }

        Directory.CreateDirectory(output);
        if (json) {
            var path = Path.Combine(output, "symbols.json");
            File.WriteAllText(path, JsonDocWriter.Render(set));
            Console.WriteLine($"wrote {path}");
            return 0;
        }

        foreach (var (name, text) in HtmlDocWriter.Render(set)) {
            File.WriteAllText(Path.Combine(output, name), text);
        }

        Console.WriteLine($"wrote {Path.Combine(output, "index.html")}: {set.Modules.Count} module(s), {set.Entries.Count} declaration(s)");
        return 0;
    }

    /// <summary>
    ///     The documentation of the modules some files declare, compiled with
    ///     the library they import; null, with the errors, if they do not compile.
    /// </summary>
    public static DocSet? ForSources(IReadOnlyList<SyntaxTree> trees, out IReadOnlyList<string> errors) {
        var library = trees.SelectMany(Library.For).Distinct().ToList();
        var compilation = Compilation.Create([..trees, ..library]);
        errors = [
            ..compilation.GetAllDiagnosticsByFile()
                .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(static d => d.Tree is { } tree ? d.Diagnostic.Format(tree.Text) : $"error[{d.Diagnostic.Id}]: {d.Diagnostic.Message}"),
        ];
        if (errors.Count > 0) {
            return null;
        }

        var modules = trees
            .Select(static t => t.Root is CompilationUnitSyntax { Module: { } module } ? module.Name.ToString().Trim() : null)
            .OfType<string>();
        return DocSet.Build(compilation, modules);
    }

    /// <summary>The documentation of every module of the standard library, the core among them.</summary>
    public static DocSet StandardLibrary() => DocSet.Build(StandardLibraryCompilation(), StandardLibraryModules);

    /// <summary>The modules of the standard library, the core first.</summary>
    public static IReadOnlyList<string> StandardLibraryModules => [Library.Core, ..Library.ModuleNames];

    /// <summary>A compilation of the whole standard library: a file that imports every module of it.</summary>
    public static Compilation StandardLibraryCompilation() {
        var everything = SyntaxTree.Parse(new SourceText(
            "module Documentation\n" + string.Concat(Library.ModuleNames.Select(static m => $"import {m}\n")),
            "documentation.sb"));
        return Compilation.Create([everything, ..Library.For(everything)]);
    }
}
