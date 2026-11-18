using System.Globalization;

namespace Sable.Syntax.Tests;

/// <summary>
///     The formatter's two obligations — idempotent, and never changing the
///     tree — checked on every Sable file in the repository that parses: the
///     standard library, the examples, and the test programs and corpora.
/// </summary>
/// <remarks>
///     Tree preservation is checked twice over: the tree's shape, and the
///     exact sequence of tokens, so a dropped parenthesis or a split operator
///     cannot hide behind a shape that happens to match. Comments survive too.
/// </remarks>
public sealed class FormatterRepositoryTests {
    static readonly string[] Roots = ["lib", "examples", "tests/programs", "tests/corpus", "tests/negative"];

    /// <summary>Documents whose <c>```sable</c> blocks are checked too: the design docs, the spec, and <c>sable explain</c>'s pages.</summary>
    static readonly string[] DocumentRoots = ["docs", "spec"];

    /// <summary>
    ///     Each file that parses, by its path; and each Markdown code block
    ///     that does, as <c>path#n</c> for its n-th block.
    /// </summary>
    public static TheoryData<string> Files {
        get {
            var data = new TheoryData<string>();
            foreach (var root in Roots) {
                foreach (var file in Directory.GetFiles(Path.Combine(TestHelpers.RepositoryRoot, root), "*.sb", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
                    if (Parses(File.ReadAllText(file))) {
                        data.Add(Relative(file));
                    }
                }
            }

            foreach (var root in DocumentRoots) {
                foreach (var file in Directory.GetFiles(Path.Combine(TestHelpers.RepositoryRoot, root), "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
                    var blocks = Blocks(File.ReadAllText(file));
                    for (var i = 0; i < blocks.Count; i++) {
                        if (Parses(blocks[i])) {
                            data.Add($"{Relative(file)}#{i}");
                        }
                    }
                }
            }

            return data;
        }
    }

    static string Relative(string file) => Path.GetRelativePath(TestHelpers.RepositoryRoot, file).Replace('\\', '/');

    static bool Parses(string text) => !SyntaxTree.Parse(new(text)).Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>The <c>```sable</c> blocks of a Markdown file, in order.</summary>
    static List<string> Blocks(string markdown) {
        var blocks = new List<string>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++) {
            if (lines[i].Trim() != "```sable") {
                continue;
            }

            var end = Array.FindIndex(lines, i + 1, static l => l.Trim() == "```");
            if (end < 0) {
                break;
            }

            blocks.Add(string.Join('\n', lines[(i + 1)..end]) + "\n");
            i = end;
        }

        return blocks;
    }

    static string Read(string file) {
        var hash = file.LastIndexOf('#');
        return hash < 0
            ? File.ReadAllText(Path.Combine(TestHelpers.RepositoryRoot, file))
            : Blocks(File.ReadAllText(Path.Combine(TestHelpers.RepositoryRoot, file[..hash])))[int.Parse(file[(hash + 1)..], CultureInfo.InvariantCulture)];
    }

    [Fact]
    public void KeepsACommentInsideAnEmptyBodyOffTheClosingBrace() {
        // The '}' once landed inside the comment, ending the body a line late.
        Assert.Equal("fn f() { // nothing yet\n}\n", SyntaxFormatter.Format("fn f() {   // nothing yet\n}"));
        Assert.Equal("fn f() { /* nothing */\n}\n", SyntaxFormatter.Format("fn f() { /* nothing */ }"));
    }

    [Theory]
    // An inactive region's first line once grew a blank line on every pass.
    [InlineData("module E\n\n#if A\nfn f() {\n    return\n}\n#endif\n")]
    // Its lines keep their own indentation, and its blank lines stay single.
    [InlineData("module E\n\nfn f() {\n    #if A\n    let x = 1\n\n    let y = 2\n    #else\n    let z = 3\n    #endif\n}\n")]
    [InlineData("module E\n\nfn f() {\n#if A\n    let x = 1\n#endif\n}\n")]
    public void KeepsConditionalRegionsAsWritten(string source) {
        var formatted = SyntaxFormatter.Format(source);
        Assert.Equal(formatted, SyntaxFormatter.Format(formatted));
        Assert.DoesNotContain(SyntaxTree.Parse(new(formatted)).Diagnostics, static d => d.Severity == DiagnosticSeverity.Error);
        if (source.Contains("let x", StringComparison.Ordinal)) {
            Assert.Contains("\n    let x = 1\n", formatted, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("        let", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIndentedDirectiveClosesAnInactiveRegion() {
        // The lexer once read '    #endif' in an inactive region as more disabled text.
        var tree = SyntaxTree.Parse(new("module E\n\nfn f() {\n    #if A\n    let x = 1\n    #endif\n}\n"));
        Assert.Empty(tree.Diagnostics);
    }

    /// <summary>The directories whose files are kept formatted: <c>sable fmt --check</c> is clean on each.</summary>
    /// <remarks>
    ///     Not <c>tests/negative</c>: its expectation comments sit in a column
    ///     of their own, which the formatter would close up.
    /// </remarks>
    public static TheoryData<string> FormattedFiles {
        get {
            var data = new TheoryData<string>();
            foreach (var root in (string[])["lib", "examples", "tests/corpus", "tests/programs"]) {
                foreach (var file in Directory.GetFiles(Path.Combine(TestHelpers.RepositoryRoot, root), "*.sb", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
                    if (Parses(File.ReadAllText(file))) {
                        data.Add(Relative(file));
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FormattedFiles))]
    public void TheRepositoryIsFormatted(string file) {
        var source = Read(file);
        var formatted = SyntaxFormatter.Format(source);
        Assert.True(source == formatted, $"{file} is not formatted (run 'sable fmt {file}'); first difference:\n{FirstDifference(source, formatted)}");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FormattingIsIdempotent(string file) {
        var once = SyntaxFormatter.Format(Read(file));
        var twice = SyntaxFormatter.Format(once);
        Assert.True(once == twice, $"formatting {file} is not idempotent; first difference:\n{FirstDifference(once, twice)}");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FormattingPreservesTheTree(string file) {
        var source = Read(file);
        var formatted = SyntaxFormatter.Format(source);

        var before = SyntaxTree.Parse(new(source, "before"));
        var after = SyntaxTree.Parse(new(formatted, "after"));

        Assert.DoesNotContain(after.Diagnostics, static d => d.Severity == DiagnosticSeverity.Error);
        Assert.True(
            TreeDump.Dump(before.Root) == TreeDump.Dump(after.Root),
            $"formatting {file} changed the tree; first difference:\n{FirstDifference(source, formatted)}"
        );

        var tokensBefore = before.Root.DescendantTokens().Select(static t => t.Text).ToList();
        var tokensAfter = after.Root.DescendantTokens().Select(static t => t.Text).ToList();
        Assert.Equal(tokensBefore, tokensAfter);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FormattingKeepsEveryComment(string file) {
        var source = Read(file);
        var formatted = SyntaxFormatter.Format(source);
        Assert.Equal(Comments(source), Comments(formatted));
    }

    static List<string> Comments(string text) {
        var tree = SyntaxTree.Parse(new(text));
        return [
            ..tree.Root.DescendantTokens()
                .SelectMany(static t => t.LeadingTrivia.Concat(t.TrailingTrivia))
                .Where(static t => t.IsComment)
                .Select(static t => t.Text.TrimEnd()),
        ];
    }

    /// <summary>The first line two texts disagree on, with its neighbours, for a readable failure.</summary>
    static string FirstDifference(string a, string b) {
        var left = a.Split('\n');
        var right = b.Split('\n');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++) {
            var l = i < left.Length ? left[i] : "<end>";
            var r = i < right.Length ? right[i] : "<end>";
            if (l != r) {
                return $"line {i + 1}:\n- {l}\n+ {r}";
            }
        }

        return "(none)";
    }
}
