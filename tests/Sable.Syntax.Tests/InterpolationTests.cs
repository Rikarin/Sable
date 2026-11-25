namespace Sable.Syntax.Tests;

/// <summary>
///     Interpolated strings. The lexer keeps one token, because holes nest
///     arbitrarily; the parser takes it apart again so the holes are real
///     expressions. The pieces must tile the token exactly.
/// </summary>
public sealed class InterpolationTests {
    static InterpolatedStringExpressionSyntax Parse(string source) {
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        return Assert.IsType<InterpolatedStringExpressionSyntax>(tree.Root);
    }

    static IReadOnlyList<ExpressionSyntax> Holes(
        InterpolatedStringExpressionSyntax node
    ) =>
        [..node.Contents.OfType<InterpolationSyntax>().Select(static h => h.Expression)];

    [Fact]
    public void ParsesASingleHole() {
        var node = Parse("\"value: \\(x)\"");
        Assert.Equal(2, node.Contents.Count);
        Assert.Equal("value: ", Assert.IsType<InterpolatedTextSyntax>(node.Contents[0]).TextToken.Text);
        Assert.Equal(SyntaxKind.IdentifierName, Holes(node)[0].Kind);
    }

    [Fact]
    public void ParsesSeveralHoles() {
        var node = Parse("\"\\(first.name), \\(second.name)\"");
        Assert.Equal(2, Holes(node).Count);
        Assert.All(Holes(node), static h => Assert.Equal(SyntaxKind.MemberAccessExpression, h.Kind));
    }

    [Fact]
    public void ParsesAnExpressionInAHole() {
        var node = Parse("\"sum: \\(a + b * 2)\"");
        Assert.Equal(
            "(Binary (IdentifierName a) + (Binary (IdentifierName b) * (Literal 2)))",
            TreeDump.Dump(Holes(node)[0])
        );
    }

    [Fact]
    public void ParsesACallInAHole() =>
        Assert.Equal(
            SyntaxKind.InvocationExpression,
            Holes(Parse("\"\\(p.length())\""))[0].Kind
        );

    [Fact]
    public void ParsesAMetatypeInAHole() =>
        Assert.Equal(
            SyntaxKind.MemberAccessExpression,
            Holes(Parse("\"\\(Self.self)\""))[0].Kind
        );

    [Fact]
    public void ParsesNestedStringsInAHole() {
        var node = Parse("\"outer \\(f(\"inner\"))\"");
        Assert.Equal(SyntaxKind.InvocationExpression, Holes(node)[0].Kind);
    }

    [Fact]
    public void ParsesAHoleInAMultilineString() {
        const string source = "\"\"\"\n  a \\(x)\n  b\n  \"\"\"";
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());

        var node = Assert.IsType<InterpolatedStringExpressionSyntax>(tree.Root);
        Assert.Single(Holes(node));
    }

    [Fact]
    public void KeepsEscapesInTheLiteralPieces() {
        var node = Parse("\"a\\tb \\(x) c\\nd\"");
        var first = Assert.IsType<InterpolatedTextSyntax>(node.Contents[0]);

        // The token text is the raw characters; its value is the cooked text.
        Assert.Equal("""a\tb """, first.TextToken.Text);
        Assert.Equal("a\tb ", first.TextToken.Value);
    }

    [Fact]
    public void HoleWhitespaceIsPreserved() {
        const string source = "\"\\(  x  )\"";
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
    }

    [Fact]
    public void SpansTileTheWholeNode() {
        var node = Parse("\"a \\(x) b \\(y) c\"");

        var expected = node.FullSpan.Start;
        foreach (var child in node.ChildNodesAndTokens()) {
            Assert.Equal(expected, child.FullSpan.Start);
            expected = child.FullSpan.End;
        }

        Assert.Equal(node.FullSpan.End, expected);
    }

    [Fact]
    public void DiagnosticsInsideAHolePointAtTheFile() {
        const string source = "let s = \"value: \\(a + )\"";
        var tree = SyntaxTree.ParseStatement(source);

        var diagnostic = Assert.Single(
            tree.Diagnostics,
            static d => d.Code == ErrorCode.ERR_ExpectedExpression
        );

        // The ')' that ends the hole is at index 22 of the statement.
        Assert.Equal(22, diagnostic.Span.Start);
        Assert.Contains("1:23", diagnostic.Format(tree.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedHoleStillRoundTrips() {
        string[] sources = [
            "\"\\(\"", "\"\\()\"", "\"\\(a +)\"", "\"\\(((\"", "\"a \\(b\""
        ];

        foreach (var source in sources) {
            var tree = SyntaxTree.ParseExpression(source);
            Assert.True(
                source == tree.ToFullString(),
                $"round-trip failed for {source}: {tree.ToFullString()}"
            );
        }
    }

    [Fact]
    public void FormattingLeavesInterpolationAlone() {
        const string source = """fn f() { print("a \(x+1) b") }""";
        var formatted = SyntaxFormatter.Format(source);
        Assert.Contains("\"a \\(x + 1) b\"", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingAnInterpolatedStringIsIdempotent() {
        const string source = "let s = \"\\(a) \\(b.c()) \\(d + e)\"";
        var once = SyntaxFormatter.Format(source);
        Assert.Equal(once, SyntaxFormatter.Format(once));
    }

    static InterpolationSyntax OnlyHole(string source) =>
        Assert.Single(Parse(source).Contents.OfType<InterpolationSyntax>());

    [Fact]
    public void AColonAfterTheExpressionStartsAFormatSpec() {
        var hole = OnlyHole("\"\\(price:>10.2)\"");
        Assert.Equal(SyntaxKind.IdentifierName, hole.Expression.Kind);
        Assert.Equal(SyntaxKind.InterpolationFormatToken, hole.FormatToken.Kind);
        Assert.Equal(":>10.2", hole.FormatToken.Text);
        Assert.Equal(">10.2", hole.FormatToken.Value);
    }

    [Fact]
    public void AFormatSpecIsTextNotTokens() {
        // What would lex as an unfinished char literal, or a number run into
        // a letter, is the binder's to read; the parser says nothing of it.
        Assert.Equal(":`^8", OnlyHole("\"\\(x:`^8) \"").FormatToken.Text);
        Assert.Equal("08x", OnlyHole("\"\\(n:08x)\"").FormatToken.Value);
        Assert.Equal(" d", OnlyHole("\"\\(n: d)\"").FormatToken.Value);
    }

    [Fact]
    public void ATernarysColonIsTheExpressions() {
        var plain = OnlyHole("\"\\(ok ? a : b)\"");
        Assert.Equal(SyntaxKind.ConditionalExpression, plain.Expression.Kind);
        Assert.True(plain.FormatToken.IsNone);

        var formatted = OnlyHole("\"\\(ok ? a : b:x)\"");
        Assert.Equal(SyntaxKind.ConditionalExpression, formatted.Expression.Kind);
        Assert.Equal("x", formatted.FormatToken.Value);
    }

    [Fact]
    public void AHoleWithoutASpecHasNoFormatToken() =>
        Assert.True(OnlyHole("\"\\(f(a: 1))\"").FormatToken.IsNone);

    [Fact]
    public void FormattingLeavesAFormatSpecAlone() {
        const string source = """fn f() { print("\(x :>8) \(y:+.2) \(z: d)") }""";
        var formatted = SyntaxFormatter.Format(source);
        Assert.Contains("\"\\(x:>8) \\(y:+.2) \\(z: d)\"", formatted, StringComparison.Ordinal);
        Assert.Equal(formatted, SyntaxFormatter.Format(formatted));
    }

    /// <summary>
    ///     Text between two holes that means nothing has no value, and is in
    ///     the tree all the same.
    /// </summary>
    [Theory]
    [InlineData("\"\\(a)\\u{75807f}\\(b)\"")]
    [InlineData("\"\\(a)\\u{75807f}\\(")]
    [InlineData("\"\\(a)\\u{41\\(")]
    [InlineData("\"\\(a)\\u{41\\(b)\"")]
    [InlineData("\"\\(a)\\u{41}\\(")]
    [InlineData("\"\\u{75807f}\\(")]
    [InlineData("\"\\(a)\\u\\(b)\"")]
    [InlineData("\"\\(a)\\u{}\\(b)\"")]
    [InlineData("\"\\(a)\\u{d800}\\(b)\"")]
    [InlineData("\"\\(a)\\u{75807f}")]
    [InlineData("\"\\(a)\\")]
    [InlineData("\"\\(())\\u{75807f\\(")]
    [InlineData("\"\"\"\n\\(a)\\u{75807f}\\(b)\n\"\"\"")]
    [InlineData("\"\"\"\n\\(a)\\\n\\(b)\n\"\"\"")]
    [InlineData("\"\"\"\n\\(a)\\u{41\\(")]
    public void TextWithNoValueBetweenHolesIsKept(string literal) {
        foreach (var source in (string[])[$"fn f() {{ let x = {literal}", $"fn f() {{ let x = {literal}\n}}\n", "fn{" + literal]) {
            Assert.Equal(source, SyntaxTree.Parse(source).ToFullString());

            // As typed: what was lexed before the edit is spliced back in.
            for (var at = 0; at < source.Length; at++) {
                var before = SyntaxTree.Parse(source.Remove(at, 1));
                var incremental = before.WithChange(new(new(at, 0), source[at..(at + 1)]));
                Assert.Equal(source, incremental.ToFullString());
                Assert.Equal(TreeDump.Dump(SyntaxTree.Parse(source).Root), TreeDump.Dump(incremental.Root));
            }
        }
    }

    /// <summary>The fuzzer's: an edit that inserts the text.</summary>
    [Theory]
    [InlineData("\"}\\(d(t]))\\u{32767f}\\(")]
    [InlineData("\"\\(())\\u{75807f\\(")]
    public void AnEditThatInsertsTextWithNoValue(string inserted) {
        const string source = "module M\nfn f() { let x = 1 }\nfn g() { }\n";
        var tree = SyntaxTree.Parse(source);
        for (var at = 0; at <= source.Length; at++) {
            Assert.Equal(source.Insert(at, inserted), tree.WithChange(new(new(at, 0), inserted)).ToFullString());
        }
    }

    [Fact]
    public void AJoinedLineBetweenHolesHasNoValue() {
        var node = Parse("\"\"\"\n    \\(a)\\\n    \\(b)\n    \"\"\"");
        Assert.Equal(2, Holes(node).Count);
        Assert.Equal("", string.Concat(node.Contents.OfType<InterpolatedTextSyntax>().Select(static t => t.TextToken.Value)));
    }
}
