using Sable.Driver.Packages;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable lint</c>: the lint set of docs/05 §5.7, over a package or some files.
/// </summary>
/// <remarks>
///     In a package, and given no paths, it lints the package's own files —
///     its library and each binary's entry file — compiled with their
///     dependencies, at its <c>[lints]</c> table's levels; the command line's
///     levels go over those. Given paths, the files are linted as they are,
///     at the default levels and the command line's.
/// </remarks>
public sealed class LintCommand : PackageCommand {
    public override string Name => "lint";
    public override string Summary => "run the lints over the package (or some files)";

    public override string Usage => $"""
        usage: sable lint [-p <package>] [--allow <lint>] [--warn <lint>] [--error <lint>]
               sable lint <paths...> [--allow <lint>] [--warn <lint>] [--error <lint>]
               sable lint --list

          (no paths)       in a package: its own files, at its [lints] levels
          paths            .sb files, or directories of them (outside a package: the current directory)
          -p <name>        the workspace member to lint (default: the one you are in, else all)
          --allow <lint>   turn a lint off
          --warn <lint>    report it as a warning
          --error <lint>   report it as an error, and fail
          --list           list the lints with their default levels

          The lints: {string.Join(", ", Lints.All.Select(static l => l.Name))}
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var levels = new List<(string Name, LintLevel Level)>();
        var paths = new List<string>();
        string? package = null;
        for (var i = 0; i < arguments.Count; i++) {
            if (arguments[i] == "--list") {
                foreach (var lint in Lints.All) {
                    Console.WriteLine($"{lint.Name,-20} {lint.DefaultLevel.ToString().ToLowerInvariant(),-6} S{(int)lint.Code:D4}  {lint.Description}");
                }

                return 0;
            }

            if (arguments[i] is "--allow" or "--warn" or "--error" && i + 1 < arguments.Count) {
                var level = LintConfiguration.ParseLevel(arguments[i][2..])!.Value;
                var name = arguments[++i];
                if (Lints.Find(name) is null) {
                    Console.Error.WriteLine($"sable lint: no lint named '{name}'; 'sable lint --list' lists them");
                    return 2;
                }

                levels.Add((name, level));
                continue;
            }

            if (arguments[i] is "-p" or "--package" && i + 1 < arguments.Count) {
                package = arguments[++i];
                continue;
            }

            if (arguments[i].StartsWith('-')) {
                return BadUsage();
            }

            paths.Add(arguments[i]);
        }

        LintConfiguration Levels(LintConfiguration configuration) => levels.Aggregate(configuration, static (c, l) => c.With(l.Name, l.Level));

        if (paths.Count == 0 && Workspace.FindManifestDirectory(Directory.GetCurrentDirectory()) is not null) {
            return Guard(() => LintPackage(package, Levels));
        }

        if (package is not null) {
            return BadUsage();
        }

        var files = FmtCommand.SourceFiles(paths.Count == 0 ? ["."] : paths).ToList();
        if (files.Count == 0) {
            Console.Error.WriteLine("sable lint: no .sb files");
            return 1;
        }

        var trees = files.Select(static f => SyntaxTree.Parse(new SourceText(File.ReadAllText(f), f))).ToList();
        var outcome = Lint(trees, trees, Levels(LintConfiguration.Default));
        if (outcome is null) {
            Console.Error.WriteLine("sable lint: the files do not compile; fix the errors first");
            return 1;
        }

        return Summarise(outcome.Value.Found, outcome.Value.Failed);
    }

    /// <summary>The lints of each selected member, over its own files, compiled with its dependencies.</summary>
    static int LintPackage(string? package, Func<LintConfiguration, LintConfiguration> levels) {
        var workspace = LoadWorkspace();
        var environment = Environment;
        var graph = PackageBuilder.Resolve(workspace, environment, new(Package: package));
        var found = 0;
        var failed = 0;
        foreach (var member in PackageBuilder.Selected(workspace, package)) {
            var configuration = levels(member.Manifest.Lints);
            var targets = new List<BinTarget?>();
            if (member.LibrarySources().Count > 0) {
                targets.Add(null);
            }

            targets.AddRange(member.Bins);
            foreach (var target in targets) {
                // As 'sable check' lints: the library's files with the library,
                // a binary's entry file with the binary — each file once.
                var program = PackageBuilder.Program(graph, member.Name, target);
                var own = member.LibrarySources().Count;
                IReadOnlyList<SyntaxTree> linted = target is null ? [..program.Take(own)] : [program[0]];
                if (Lint(program, linted, configuration) is not var (lints, errors)) {
                    Console.Error.WriteLine($"error: could not lint {member.Name}: it does not compile; fix the errors first");
                    return 1;
                }

                found += lints;
                failed += errors;
            }
        }

        return Summarise(found, failed);
    }

    /// <summary>Compiles a program and prints the lints of <paramref name="linted" />; null, having printed the errors, when it does not compile.</summary>
    static (int Found, int Failed)? Lint(IReadOnlyList<SyntaxTree> program, IReadOnlyList<SyntaxTree> linted, LintConfiguration configuration) {
        var compilation = Compilation.Create([..program, ..Library.For(program)]);
        var errors = compilation.GetAllDiagnosticsByFile()
            .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error && d.Tree is not null)
            .ToList();
        foreach (var (error, tree) in errors) {
            Console.Error.WriteLine(error.Format(tree!.Text));
        }

        if (errors.Count > 0) {
            return null;
        }

        var found = Lints.RunByFile(compilation, configuration, linted);
        foreach (var (diagnostic, tree) in found) {
            Console.WriteLine(diagnostic.Format(tree.Text));
            Console.WriteLine($"   = 'sable explain {diagnostic.Id}' for more");
        }

        return (found.Count, found.Count(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error));
    }

    static int Summarise(int found, int failed) {
        if (found > 0) {
            Console.WriteLine($"{found} lint(s), {failed} of them error(s)");
        }

        return failed > 0 ? 1 : 0;
    }
}
