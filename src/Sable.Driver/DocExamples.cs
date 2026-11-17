using System.Text;
using System.Text.RegularExpressions;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>
///     <c>sable test --doc</c> (spec/SYNTAX.md §25): the fenced <c>sable</c>
///     blocks of doc comments, each made into a <c>@test</c> function of the
///     module whose file it is in.
/// </summary>
/// <remarks>
///     <para>
///         An example sees what the file it documents sees: the file's
///         imports are copied, and Sable.Testing's added, so it can
///         <c>assert</c>. It is a function of that module, so it reaches the
///         module's internal declarations. A block whose info string says
///         <c>ignore</c> (<c>```sable ignore</c>) is left out.
///     </para>
///     <para>
///         The file written for an example has the name of the file it came
///         from, and its code on the lines it was written on, so that an
///         error in an example points where the example is.
///     </para>
/// </remarks>
public static partial class DocExamples {
    /// <summary>A file of examples for each one the tree's doc comments hold; none when they hold none.</summary>
    public static IReadOnlyList<SyntaxTree> Extract(SyntaxTree tree) {
        if (tree.Root is not CompilationUnitSyntax { Module: { } module } unit) {
            return [];
        }

        var lines = tree.Text.Text.Split('\n');
        var imports = string.Join("\n", unit.Imports.Select(static i => i.ToString().Trim()));
        var header = $"module {module.Name.ToString().Trim()}\n{imports}\nimport Sable.Testing\n";
        var headerLines = header.Count(static c => c == '\n');

        var files = new List<SyntaxTree>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++) {
            if (!IsDocLine(lines[i])) {
                continue;
            }

            // A run of doc comment lines, and what it documents: the first
            // line after it that is not an attribute.
            var start = i;
            while (i < lines.Length && IsDocLine(lines[i])) {
                i++;
            }

            var end = i;
            var documented = "file";
            for (var j = end; j < lines.Length; j++) {
                var text = lines[j].Trim();
                if (text.Length == 0 || text.StartsWith('@')) {
                    continue;
                }

                documented = DeclaredName().Match(text) is { Success: true } match ? match.Groups["name"].Value : "item";
                break;
            }

            foreach (var (line, code) in Blocks(lines, start, end)) {
                var n = counts[documented] = counts.GetValueOrDefault(documented) + 1;
                var name = $"doc_{documented}_{n}";

                // The '@test fn' line just above the code, which then starts
                // on the line it was written on, when the header leaves room.
                var text = new StringBuilder(header);
                for (var pad = headerLines; pad < line - 2; pad++) {
                    text.Append('\n');
                }

                text.Append("@test fn ").Append(name).Append("() {\n").Append(code).Append("}\n");
                files.Add(SyntaxTree.Parse(new SourceText(text.ToString(), tree.Text.FileName)));
            }
        }

        return files;
    }

    static bool IsDocLine(string line) {
        var text = line.TrimStart();
        return text.StartsWith("///", StringComparison.Ordinal) && !text.StartsWith("////", StringComparison.Ordinal);
    }

    /// <summary>The <c>sable</c> blocks among the doc lines: the 0-based line each one's code starts on, and the code.</summary>
    static IEnumerable<(int Line, string Code)> Blocks(string[] lines, int start, int end) {
        for (var i = start; i < end; i++) {
            var text = Content(lines[i]).Trim();
            if (!text.StartsWith("```", StringComparison.Ordinal)) {
                continue;
            }

            var info = text[3..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var wanted = info is ["sable", ..] && !info.Contains("ignore");
            var code = new StringBuilder();
            var first = i + 1;
            for (i++; i < end && !Content(lines[i]).Trim().StartsWith("```", StringComparison.Ordinal); i++) {
                code.Append(Content(lines[i])).Append('\n');
            }

            if (wanted) {
                yield return (first + 1, code.ToString());
            }
        }
    }

    /// <summary>A doc line without its '///' and the one space after it.</summary>
    static string Content(string line) {
        var text = line.TrimStart()[3..].TrimEnd('\r');
        return text.StartsWith(' ') ? text[1..] : text;
    }

    [GeneratedRegex(@"\b(fn|struct|class|union|enum|protocol|actor|extension|var|let|const|init|operator)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DeclaredName();
}
