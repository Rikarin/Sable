using Sable.Driver.Docs;
using Sable.Driver.Packages;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable doc</c>: documentation from <c>///</c> comments for the public
///     declarations of a package's library (docs/05 §5.1) — or of the modules
///     some files declare, or of the standard library — as static HTML, or
///     with <c>--json</c> as a symbol graph.
/// </summary>
/// <remarks>
///     In a package, and given no paths, it documents the package as a build
///     sees it: its library's modules, compiled with its dependencies and its
///     default features — not its <c>tests/</c>, and not its dependencies'
///     modules, which are theirs to document.
/// </remarks>
public sealed class DocCommand : PackageCommand {
    public override string Name => "doc";
    public override string Summary => "generate documentation, or a symbol graph";

    public override string Usage => """
        usage: sable doc [-p <package>] [--out <dir>] [--json] [--open]
               sable doc <paths...> [--out <dir>] [--json] [--open]
               sable doc --std [--out <dir>] [--json] [--open]

          (no paths)   in a package: its library's modules, built with its dependencies
          paths        .sb files, or directories of them; the modules they declare are documented
          -p <name>    the workspace member to document (default: the one you are in)
          --std        document the standard library instead
          --out dir    where to write it (default: target/doc, at the workspace's root in a package)
          --json       write the symbol graph, symbols.json, instead of HTML
          --open       open what was written, in $BROWSER or the system's browser
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var paths = new List<string>();
        string? output = null;
        var json = false;
        var open = false;
        var standardLibrary = false;
        string? package = null;
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--out" when i + 1 < arguments.Count: output = arguments[++i]; break;
                case "-p" or "--package" when i + 1 < arguments.Count: package = arguments[++i]; break;
                case "--json": json = true; break;
                case "--open": open = true; break;
                case "--std": standardLibrary = true; break;
                default:
                    if (arguments[i].StartsWith('-')) {
                        return BadUsage();
                    }

                    paths.Add(arguments[i]);
                    break;
            }
        }

        if (paths.Count > 0 && (standardLibrary || package is not null) || standardLibrary && package is not null) {
            return BadUsage();
        }

        DocSet set;
        if (standardLibrary) {
            set = StandardLibrary();
        } else if (paths.Count == 0 && Workspace.FindManifestDirectory(Directory.GetCurrentDirectory()) is not null) {
            DocSet? documented = null;
            var failed = Guard(() => {
                var workspace = LoadWorkspace();
                output ??= Path.Combine(workspace.TargetDirectory, "doc");
                documented = ForPackage(workspace, Environment, Member(workspace, package), out var errors);
                foreach (var error in errors) {
                    Console.Error.WriteLine(error);
                    Console.Error.WriteLine();
                }

                DiagnosticDocs.WriteHint(Console.Error, errors);
                return documented is null ? 1 : 0;
            });
            if (failed != 0) {
                return failed;
            }

            set = documented!;
        } else if (package is not null) {
            Console.Error.WriteLine("sable doc: -p names a workspace member, and there is no package here");
            return 1;
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

        output ??= Path.Combine("target", "doc");
        Directory.CreateDirectory(output);
        string written;
        if (json) {
            written = Path.Combine(output, "symbols.json");
            File.WriteAllText(written, JsonDocWriter.Render(set));
            Console.WriteLine($"wrote {written}");
        } else {
            foreach (var (name, text) in HtmlDocWriter.Render(set)) {
                File.WriteAllText(Path.Combine(output, name), text);
            }

            written = Path.Combine(output, "index.html");
            Console.WriteLine($"wrote {written}: {set.Modules.Count} module(s), {set.Entries.Count} declaration(s)");
        }

        return open ? Browser.Open(Path.GetFullPath(written)) : 0;
    }

    /// <summary>
    ///     The documentation of a package: its library's modules (or, with no
    ///     library, its first binary's), compiled as a build compiles them;
    ///     null, with the errors, when they do not compile.
    /// </summary>
    public static DocSet? ForPackage(Workspace workspace, PackageEnvironment environment, LocalPackage member, out IReadOnlyList<string> errors) {
        var graph = PackageBuilder.Resolve(workspace, environment, new(Package: member.Name));
        var bin = member.LibrarySources().Count == 0 ? member.Bins.FirstOrDefault() : null;
        var program = PackageBuilder.Program(graph, member.Name, bin);
        if (program.Count == 0) {
            throw new PackageException($"package '{member.Name}' has no source files to document");
        }

        // A binary's entry file comes first, then the package's own library files.
        // What its build script wrote is part of its library, and documented with it.
        var own = bin is not null ? 1 : graph.LibrarySources(graph.Packages[member.Name]).Count;
        return ForSources(program, out errors, [..program.Take(own)]);
    }

    /// <summary>
    ///     The documentation of the modules some files declare, compiled with
    ///     the library they import; null, with the errors, if they do not compile.
    /// </summary>
    /// <param name="documented">The trees whose modules are documented; by default, all of them.</param>
    public static DocSet? ForSources(IReadOnlyList<SyntaxTree> trees, out IReadOnlyList<string> errors, IReadOnlyList<SyntaxTree>? documented = null) {
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

        var modules = (documented ?? trees)
            .Select(static t => t.Root is CompilationUnitSyntax { Module: { } module } ? module.Name.ToString().Trim() : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
        return DocSet.Build(compilation, [..modules]);
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
