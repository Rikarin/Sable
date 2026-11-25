using Sable.Driver.Generators;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>
///     The loop of <c>sable fix</c>: compile, lint, apply the fixes the lints
///     carry, and again over what that gives — a fix can leave something
///     else to fix, as a deleted assignment leaves a <c>var</c> that never
///     changes — until nothing more is fixed.
/// </summary>
internal static class LintFixer {
    /// <summary>Rounds before giving up on reaching a file the lints are done with.</summary>
    const int MaxRounds = 8;

    /// <summary>
    ///     Fixes the files of a program. <paramref name="parse" /> reads the
    ///     program as it is on disk, each time; <paramref name="linted" />
    ///     picks the files to lint and fix out of it, and
    ///     <paramref name="pathOf" /> says where each is. With
    ///     <paramref name="check" />, nothing is written: what would change is
    ///     said, and a change makes it fail. Returns the exit code.
    /// </summary>
    public static int Run(
        Func<IReadOnlyList<SyntaxTree>> parse,
        Func<IReadOnlyList<SyntaxTree>, IReadOnlyList<SyntaxTree>> linted,
        Func<SyntaxTree, string> pathOf,
        LintConfiguration configuration,
        bool check,
        TextWriter output,
        TextWriter error
    ) {
        var fixedCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var round = 0; round < MaxRounds; round++) {
            var program = parse();
            var compilation = Compilation.Create([..LibraryGenerators.Expand(program).Program, ..Library.For(program)]);
            var errors = compilation.GetAllDiagnosticsByFile()
                .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error && d.Tree is not null && !CoreLibrary.Contains(d.Tree))
                .ToList();
            if (errors.Count > 0) {
                foreach (var (diagnostic, tree) in errors.Take(10)) {
                    error.WriteLine(diagnostic.Span.End <= tree!.Text.Length ? diagnostic.Format(tree.Text) : $"error[{diagnostic.Id}]: {diagnostic.Message}");
                }

                if (round == 0) {
                    error.WriteLine("sable fix: the program does not compile; fix the errors first");
                    return 1;
                }

                // A fix should never break a program; if one did, the files go back as they were.
                foreach (var (path, text) in before) {
                    File.WriteAllText(path, text);
                }

                error.WriteLine("sable fix: the fixes left the program not compiling, so the files are as they were before them; please report this");
                return 1;
            }

            var changed = 0;
            foreach (var group in Lints.RunByFile(compilation, configuration, linted(program)).GroupBy(static d => d.Tree)) {
                var (text, applied) = LintFixes.Apply(group.Key.Text.Text, group.Select(static d => d.Diagnostic));
                if (applied == 0) {
                    continue;
                }

                var path = pathOf(group.Key);
                if (check) {
                    output.WriteLine($"{group.Key.Text.FileName}: {applied} fix(es) to make");
                    foreach (var diagnostic in group.Select(static d => d.Diagnostic).Where(static d => !d.Fixes.IsEmpty)) {
                        var line = group.Key.Text.GetLinePosition(diagnostic.Span.Start).Line;
                        output.WriteLine($"  {line}: {diagnostic.Fixes[0].Title} ({diagnostic.Message})");
                    }
                } else {
                    before.TryAdd(path, group.Key.Text.Text);
                    File.WriteAllText(path, text);
                }

                fixedCount[group.Key.Text.FileName] = fixedCount.GetValueOrDefault(group.Key.Text.FileName) + applied;
                changed += applied;
            }

            if (changed == 0 || check) {
                break;
            }
        }

        foreach (var (file, count) in fixedCount.OrderBy(static p => p.Key, StringComparer.Ordinal)) {
            if (!check) {
                output.WriteLine($"    Fixed {count} in {file}");
            }
        }

        var total = fixedCount.Values.Sum();
        if (check) {
            return total > 0 ? 1 : 0;
        }

        output.WriteLine(total == 0 ? "    Nothing to fix" : $"    Fixed {total} lint(s) in {fixedCount.Count} file(s)");
        return 0;
    }
}
