using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable lint</c>: the lint set of docs/05 §5.7 over some files.
/// </summary>
/// <remarks>
///     For loose files, with levels from the command line. A package's
///     lints run with <c>sable check</c>, at its <c>[lints]</c> table's levels.
/// </remarks>
public sealed class LintCommand : Command {
    public override string Name => "lint";
    public override string Summary => "run the lints over loose source files ('check' lints a package)";

    public override string Usage => $"""
        usage: sable lint [paths...] [--allow <lint>] [--warn <lint>] [--error <lint>] [--plugin <dll>] [--list]

          paths            .sb files, or directories of them (default: the current directory)
          --allow <lint>   turn a lint off
          --warn <lint>    report it as a warning
          --error <lint>   report it as an error, and fail
          --plugin <dll>   load more lints from a plugin (docs/05 §5.7)
          --list           list the lints with their default levels
          'sable fix' applies the fixes the lints suggest.

          The lints: {string.Join(", ", Lints.All.Select(static l => l.Name))}
        """;

    /// <summary>
    ///     Reads the options <c>sable lint</c> and <c>sable fix</c> share: the
    ///     plugins, then the levels (which may name a plugin's lints), and
    ///     the paths. Null when they are all right; otherwise the exit code,
    ///     having said what is wrong.
    /// </summary>
    internal static int? ReadOptions(IReadOnlyList<string> arguments, string command, out List<string> paths, out LintConfiguration configuration) {
        paths = [];
        configuration = LintConfiguration.Default;
        for (var i = 0; i < arguments.Count; i++) {
            if (arguments[i] != "--plugin") {
                continue;
            }

            if (i + 1 >= arguments.Count) {
                Console.Error.WriteLine($"sable {command}: '--plugin' needs the plugin's path");
                return 2;
            }

            try {
                configuration = configuration.WithPlugin(Lints.LoadPlugin(arguments[++i]));
            } catch (Exception e) when (e is LintPluginException or ArgumentException) {
                Console.Error.WriteLine($"sable {command}: {e.Message}");
                return 1;
            }
        }

        for (var i = 0; i < arguments.Count; i++) {
            if (arguments[i] == "--plugin") {
                i++;
                continue;
            }

            if (arguments[i] is "--allow" or "--warn" or "--error" && i + 1 < arguments.Count) {
                var level = LintConfiguration.ParseLevel(arguments[i][2..])!.Value;
                var name = arguments[++i];
                if (configuration.Find(name) is null) {
                    Console.Error.WriteLine($"sable {command}: no lint named '{name}'; 'sable lint --list' lists them");
                    return 2;
                }

                configuration = configuration.With(name, level);
                continue;
            }

            if (arguments[i].StartsWith('-')) {
                Console.Error.WriteLine($"sable {command}: unknown option '{arguments[i]}'");
                return 2;
            }

            paths.Add(arguments[i]);
        }

        return null;
    }

    public override int Run(IReadOnlyList<string> arguments) {
        var list = arguments.Contains("--list");
        if (ReadOptions([..arguments.Where(static a => a != "--list")], "lint", out var paths, out var configuration) is { } failure) {
            return failure;
        }

        if (list) {
            foreach (var lint in configuration.All) {
                Console.WriteLine($"{lint.Name,-20} {lint.DefaultLevel.ToString().ToLowerInvariant(),-6} S{(int)lint.Code:D4}  {lint.Description}");
            }

            return 0;
        }

        var files = FmtCommand.SourceFiles(paths.Count == 0 ? ["."] : paths).ToList();
        if (files.Count == 0) {
            Console.Error.WriteLine("sable lint: no .sb files");
            return 1;
        }

        var trees = files.Select(static f => SyntaxTree.Parse(new SourceText(File.ReadAllText(f), f))).ToList();
        var compilation = Compilation.Create([..trees, ..trees.SelectMany(Library.For).Distinct()]);

        var errors = compilation.GetAllDiagnosticsByFile()
            .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error && d.Tree is not null)
            .ToList();
        foreach (var (error, tree) in errors) {
            Console.Error.WriteLine(error.Format(tree!.Text));
        }

        if (errors.Count > 0) {
            Console.Error.WriteLine("sable lint: the files do not compile; fix the errors first");
            return 1;
        }

        var found = Lints.RunByFile(compilation, configuration, trees);
        foreach (var (diagnostic, tree) in found) {
            Console.WriteLine(diagnostic.Format(tree.Text));
            Console.WriteLine($"   = 'sable explain {diagnostic.Id}' for more");
        }

        var failed = found.Count(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error);
        if (found.Count > 0) {
            Console.WriteLine($"{found.Count} lint(s), {failed} of them error(s)");
        }

        return failed > 0 ? 1 : 0;
    }
}
