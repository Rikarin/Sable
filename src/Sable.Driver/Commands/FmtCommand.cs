using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable fmt [paths…] [--check]</c>: formats Sable source in place, in
///     the one style there is (docs/05 §5.7) — no options, ever.
/// </summary>
/// <remarks>
///     A file with a syntax error is left alone and reported: formatting
///     preserves the tree, and a broken tree is not worth preserving.
/// </remarks>
public sealed class FmtCommand : Command {
    public override string Name => "fmt";
    public override string Summary => "format source files; no options, ever";

    public override string Usage => """
        usage: sable fmt [paths...] [--check]
               sable fmt -

          paths     .sb files, or directories to format recursively (default: the
                    current directory); target/ and hidden directories are skipped
          --check   change nothing; list the files that are not formatted, and exit 1
                    if there are any
          -         format standard input to standard output
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var check = false;
        var paths = new List<string>();
        foreach (var argument in arguments) {
            switch (argument) {
                case "--check": check = true; break;
                case "-" when arguments.Count == 1: return FormatStandardInput();
                default:
                    if (argument.StartsWith('-')) {
                        return BadUsage();
                    }

                    paths.Add(argument);
                    break;
            }
        }

        if (paths.Count == 0) {
            paths.Add(".");
        }

        var failed = false;
        var unformatted = 0;
        foreach (var path in paths) {
            if (!File.Exists(path) && !Directory.Exists(path)) {
                Console.Error.WriteLine($"sable fmt: no file or directory '{path}'");
                failed = true;
            }
        }

        foreach (var file in SourceFiles(paths)) {
            var text = File.ReadAllText(file);
            var tree = SyntaxTree.Parse(new SourceText(text, file));
            var errors = tree.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0) {
                foreach (var error in errors) {
                    Console.Error.WriteLine(error.Format(tree.Text));
                }

                Console.Error.WriteLine($"sable fmt: {file} has syntax errors; not formatted");
                failed = true;
                continue;
            }

            var formatted = SyntaxFormatter.Format(tree);
            if (formatted == text) {
                continue;
            }

            unformatted++;
            if (check) {
                Console.WriteLine(file);
            } else {
                File.WriteAllText(file, formatted);
            }
        }

        return failed || (check && unformatted > 0) ? 1 : 0;
    }

    static int FormatStandardInput() {
        var text = Console.In.ReadToEnd();
        var tree = SyntaxTree.Parse(new SourceText(text, "<stdin>"));
        var errors = tree.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) {
            foreach (var error in errors) {
                Console.Error.WriteLine(error.Format(tree.Text));
            }

            return 1;
        }

        Console.Write(SyntaxFormatter.Format(tree));
        return 0;
    }

    /// <summary>
    ///     The files the paths name: a file as given, and every <c>.sb</c>
    ///     file under a directory, skipping <c>target/</c> and hidden
    ///     directories, in a stable order.
    /// </summary>
    public static IEnumerable<string> SourceFiles(IEnumerable<string> paths) {
        foreach (var path in paths) {
            if (File.Exists(path)) {
                yield return path;
            } else if (Directory.Exists(path)) {
                foreach (var file in Walk(path)) {
                    yield return file;
                }
            }
        }
    }

    static IEnumerable<string> Walk(string directory) {
        foreach (var file in Directory.GetFiles(directory, "*.sb").Order(StringComparer.Ordinal)) {
            yield return file;
        }

        foreach (var child in Directory.GetDirectories(directory).Order(StringComparer.Ordinal)) {
            var name = Path.GetFileName(child);
            if (name == "target" || name.StartsWith('.')) {
                continue;
            }

            foreach (var file in Walk(child)) {
                yield return file;
            }
        }
    }
}
