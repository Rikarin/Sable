namespace Sable.Syntax.Tests;

/// <summary>
/// Interpolated strings. The lexer keeps one token, because holes nest
/// arbitrarily; the parser takes it apart again so the holes are real
/// expressions. The pieces must tile the token exactly.
/// </summary>
public sealed class InterpolationTests
{
    private static InterpolatedStringExpressionSyntax Parse(string source)
    {
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        return Assert.IsType<InterpolatedStringExpressionSyntax>(tree.Root);
    }

    private static IReadOnlyList<ExpressionSyntax> Holes(
        InterpolatedStringExpressionSyntax node) =>
        [.. node.Contents.OfType<InterpolationSyntax>().Select(h => h.Expression)];

    [Fact]
    public void ParsesASingleHole()
    {
        var node = Parse("\"value: \\(x)\"");
        Assert.Equal(2, node.Contents.Count);
        Assert.Equal("value: ", Assert.IsType<InterpolatedTextSyntax>(
            node.Contents[0]).TextToken.Text);
        Assert.Equal(SyntaxKind.IdentifierName, Holes(node)[0].Kind);
    }

    [Fact]
    public void ParsesSeveralHoles()
    {
        var node = Parse("\"\\(first.name), \\(second.name)\"");
        Assert.Equal(2, Holes(node).Count);
        Assert.All(Holes(node), h => Assert.Equal(SyntaxKind.MemberAccessExpression, h.Kind));
    }

    [Fact]
    public void ParsesAnExpressionInAHole()
    {
        var node = Parse("\"sum: \\(a + b * 2)\"");
        Assert.Equal(
            "(Binary (IdentifierName a) + (Binary (IdentifierName b) * (Literal 2)))",
            TreeDump.Dump(Holes(node)[0]));
    }

    [Fact]
    public void ParsesACallInAHole() =>
        Assert.Equal(SyntaxKind.InvocationExpression,
            Holes(Parse("\"\\(p.length())\""))[0].Kind);

    [Fact]
    public void ParsesAMetatypeInAHole() =>
        Assert.Equal(SyntaxKind.MemberAccessExpression,
            Holes(Parse("\"\\(Self.self)\""))[0].Kind);

    [Fact]
    public void ParsesNestedStringsInAHole()
    {
        var node = Parse("\"outer \\(f(\"inner\"))\"");
        Assert.Equal(SyntaxKind.InvocationExpression, Holes(node)[0].Kind);
    }

    [Fact]
    public void ParsesAHoleInAMultilineString()
    {
        const string source = "\"\"\"\n  a \\(x)\n  b\n  \"\"\"";
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());

        var node = Assert.IsType<InterpolatedStringExpressionSyntax>(tree.Root);
        Assert.Single(Holes(node));
    }

    [Fact]
    public void KeepsEscapesInTheLiteralPieces()
    {
        var node = Parse("\"a\\tb \\(x) c\\nd\"");
        var first = Assert.IsType<InterpolatedTextSyntax>(node.Contents[0]);

        // The token text is the raw characters; its value is the cooked text.
        Assert.Equal("a\\tb ", first.TextToken.Text);
        Assert.Equal("a\tb ", first.TextToken.Value);
    }

    [Fact]
    public void HoleWhitespaceIsPreserved()
    {
        const string source = "\"\\(  x  )\"";
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
    }

    [Fact]
    public void SpansTileTheWholeNode()
    {
        var node = Parse("\"a \\(x) b \\(y) c\"");

        var expected = node.FullSpan.Start;
        foreach (var child in node.ChildNodesAndTokens())
        {
            Assert.Equal(expected, child.FullSpan.Start);
            expected = child.FullSpan.End;
        }
        Assert.Equal(node.FullSpan.End, expected);
    }

    [Fact]
    public void DiagnosticsInsideAHolePointAtTheFile()
    {
        const string source = "let s = \"value: \\(a + )\"";
        var tree = SyntaxTree.ParseStatement(source);

        var diagnostic = Assert.Single(tree.Diagnostics,
            d => d.Code == ErrorCode.ERR_ExpectedExpression);

        // The ')' that ends the hole is at index 22 of the statement.
        Assert.Equal(22, diagnostic.Span.Start);
        Assert.Contains("1:23", diagnostic.Format(tree.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedHoleStillRoundTrips()
    {
        string[] sources =
        [
            "\"\\(\"", "\"\\()\"", "\"\\(a +)\"", "\"\\(((\"", "\"a \\(b\"",
        ];

        foreach (var source in sources)
        {
            var tree = SyntaxTree.ParseExpression(source);
            Assert.True(source == tree.ToFullString(),
                $"round-trip failed for {source}: {tree.ToFullString()}");
        }
    }

    [Fact]
    public void FormattingLeavesInterpolationAlone()
    {
        const string source = "fn f() { print(\"a \\(x+1) b\") }";
        var formatted = SyntaxFormatter.Format(source);
        Assert.Contains("\"a \\(x + 1) b\"", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingAnInterpolatedStringIsIdempotent()
    {
        const string source = "let s = \"\\(a) \\(b.c()) \\(d + e)\"";
        var once = SyntaxFormatter.Format(source);
        Assert.Equal(once, SyntaxFormatter.Format(once));
    }
}
