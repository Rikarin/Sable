using System.Text.RegularExpressions;

namespace Sable.Syntax.Tests;

/// <summary>
///     Phase 1's exit criterion: every construct spec/SYNTAX.md shows parses.
///     The spec's ```sable blocks are written for a reader, not a parser: some
///     are whole files, some statements, some declarations followed by a call
///     to them, and some listings of types, patterns or literals, several to a
///     line. So a block passes when it parses as a file, as the body of a
///     function, or piece by piece: each top-level piece (a line at the left
///     margin and what is indented under it) as a file or a body, and failing
///     that each item of a listing line (items are set apart by two or more
///     spaces) as a type, an expression, a statement or a match case.
///     A block that shows what is deliberately not Sable is marked in the spec
///     itself, with <c>&lt;!-- not-sable: why --&gt;</c> on the line before its
///     fence, so the exception is visible where the example is.
/// </summary>
public sealed partial class SyntaxSpecTests {
    sealed record Block(int Line, string Code, string? NotSable);

    static IReadOnlyList<Block> Blocks {
        get {
            var path = Path.Combine(TestHelpers.RepositoryRoot, "spec", "SYNTAX.md");
            var lines = File.ReadAllLines(path);
            var blocks = new List<Block>();
            for (var i = 0; i < lines.Length; i++) {
                if (lines[i].Trim() != "```sable") {
                    continue;
                }

                var start = i + 1;
                string? notSable = null;
                if (i > 0 && NotSableMarker().Match(lines[i - 1]) is { Success: true } marker) {
                    notSable = marker.Groups[1].Value;
                }

                var end = start;
                while (end < lines.Length && lines[end].Trim() != "```") {
                    end++;
                }

                blocks.Add(new(start + 1, string.Join("\n", lines[start..end]) + "\n", notSable));
                i = end;
            }

            return blocks;
        }
    }

    public static TheoryData<int> BlockLines {
        get {
            var data = new TheoryData<int>();
            foreach (var block in Blocks) {
                data.Add(block.Line);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BlockLines))]
    public void EveryCodeBlockOfTheSpecParses(int line) {
        var block = Blocks.Single(b => b.Line == line);
        var asFile = Errors(block.Code);
        var asBody = Errors(AsBody(block.Code));
        var failingPieces = asFile.Count == 0 || asBody.Count == 0
            ? []
            : Pieces(block.Code).Where(static p => !PieceParses(p)).ToList();
        var parses = asFile.Count == 0 || asBody.Count == 0 || failingPieces.Count == 0;

        if (block.NotSable is { } reason) {
            Assert.False(
                parses,
                $"SYNTAX.md:{line} is marked not-sable ({reason}) but parses; remove the marker"
            );
            return;
        }

        Assert.True(
            parses,
            $"SYNTAX.md:{line} does not parse:\n{block.Code}\nas a file:\n"
            + string.Join("\n", asFile.Take(3))
            + "\nas a function body:\n"
            + string.Join("\n", asBody.Take(3))
            + "\npieces that parse neither way:\n"
            + string.Join("\n---\n", failingPieces)
        );
    }

    [Fact]
    public void TheSpecHasCodeBlocks() => Assert.True(Blocks.Count > 40);

    static string AsBody(string code) => "fn specBlock() {\n" + code + "\n}\n";

    /// <summary>
    ///     The block cut at each line that starts at the left margin, except a
    ///     closing bracket or an `else` or `catch` going on with the piece
    ///     before, and the declaration after an attribute line.
    /// </summary>
    static List<string> Pieces(string code) {
        var pieces = new List<List<string>>();
        foreach (var line in code.Split('\n')) {
            if (line.Trim().Length == 0) {
                continue;
            }

            var continues = pieces.Count > 0
                && (char.IsWhiteSpace(line[0])
                    || line[0] is '}' or ')' or ']'
                    || line.StartsWith("else", StringComparison.Ordinal)
                    || line.StartsWith("catch", StringComparison.Ordinal)
                    || AttributeLine().IsMatch(pieces[^1][^1])
                    || CountOf(string.Join("\n", pieces[^1]), "\"\"\"") % 2 == 1);
            if (continues) {
                pieces[^1].Add(line);
            } else {
                pieces.Add([line]);
            }
        }

        return [..pieces.Select(static p => string.Join("\n", p))];
    }

    static int CountOf(string text, string what) => (text.Length - text.Replace(what, "").Length) / what.Length;

    static bool PieceParses(string piece) {
        if (Errors(piece).Count == 0 || Errors(AsBody(piece)).Count == 0) {
            return true;
        }

        // A listing: each line's items, and a line that is not yet whole
        // (a multi-line string's opening quotes) taken with the lines after.
        var pending = "";
        foreach (var line in piece.Split('\n')) {
            pending = pending.Length == 0 ? line : pending + "\n" + line;
            var items = ItemSeparator().Split(StripComment(pending).Trim())
                .Where(static i => i.Length > 0);
            if (items.All(ItemParses)) {
                pending = "";
            }
        }

        return pending.Length == 0;
    }

    static bool ItemParses(string item) =>
        Clean(SyntaxTree.ParseType(item))
        || Clean(SyntaxTree.ParseExpression(item))
        || Clean(SyntaxTree.ParseStatement(item))
        || Errors(AsBody("match x {\n" + item + " => 0\n}")).Count == 0;

    static bool Clean(SyntaxTree tree) => !tree.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>A line's trailing comment, unless the `//` is inside a string.</summary>
    static string StripComment(string text) {
        var lines = text.Split('\n');
        for (var l = 0; l < lines.Length; l++) {
            var inString = false;
            for (var i = 0; i + 1 < lines[l].Length; i++) {
                if (lines[l][i] == '"') {
                    inString = !inString;
                } else if (!inString && lines[l][i] == '/' && lines[l][i + 1] == '/') {
                    lines[l] = lines[l][..i];
                    break;
                }
            }
        }

        return string.Join("\n", lines);
    }

    static List<string> Errors(string code) {
        var source = new SourceText(code, "SYNTAX.md");
        var tree = SyntaxTree.Parse(source);
        Assert.Equal(code, tree.ToFullString());
        return [
            ..tree.Diagnostics
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.Format(source))
        ];
    }

    [GeneratedRegex(@"^\s*@[\w.]+(\(.*\))?\s*$")]
    private static partial Regex AttributeLine();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex ItemSeparator();

    [GeneratedRegex(@"^\s*<!--\s*not-sable:\s*(.*?)\s*-->\s*$")]
    private static partial Regex NotSableMarker();
}
