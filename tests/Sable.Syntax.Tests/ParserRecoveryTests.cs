using System.Text;

namespace Sable.Syntax.Tests;

/// <summary>
/// Error recovery must never lose text. A token that cannot be used becomes
/// skipped-token trivia; a token that is required but absent is inserted as a
/// zero-width missing token. Either way the tree still reproduces the source.
/// </summary>
public sealed class ParserRecoveryTests
{
    private static void AssertRoundTrips(SyntaxTree tree, string source) =>
        Assert.True(source == tree.ToFullString(),
            $"round-trip failed\n  source: {source,-40}\n  tree:   {tree.ToFullString()}");

    [Theory]
    [InlineData("1 +")]
    [InlineData("1 + ")]
    [InlineData("+")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("f(")]
    [InlineData("f(1,")]
    [InlineData("f(1, 2")]
    [InlineData("(1")]
    [InlineData("[1, 2")]
    [InlineData("a.")]
    [InlineData("a ? b")]
    [InlineData("a ? b :")]
    [InlineData("@")]
    [InlineData("1 2")]
    [InlineData("1 § 2")]
    [InlineData("a b c d")]
    [InlineData("Point { x:")]
    [InlineData("Point { x: 1,")]
    [InlineData("]]]")]
    [InlineData(")))")]
    [InlineData("// only a comment")]
    [InlineData("/* unterminated")]
    [InlineData("\"unterminated")]
    [InlineData("1 + /* mid */ 2  // end\n")]
    public void MalformedExpressionsStillRoundTrip(string source)
    {
        var tree = SyntaxTree.ParseExpression(source);
        AssertRoundTrips(tree, source);
    }

    [Theory]
    [InlineData("List<")]
    [InlineData("List<Int")]
    [InlineData("List<,>")]
    [InlineData("T[")]
    [InlineData("T[>")]
    [InlineData("(A ->")]
    [InlineData("[String:")]
    [InlineData("some")]
    [InlineData("&")]
    [InlineData("?")]
    [InlineData("Int Int")]
    public void MalformedTypesStillRoundTrip(string source)
    {
        var tree = SyntaxTree.ParseType(source);
        AssertRoundTrips(tree, source);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{ let")]
    [InlineData("{ let x")]
    [InlineData("{ let x =")]
    [InlineData("if")]
    [InlineData("if x")]
    [InlineData("if x {")]
    [InlineData("if x { } else")]
    [InlineData("guard x")]
    [InlineData("for")]
    [InlineData("for x in")]
    [InlineData("for x in xs {")]
    [InlineData("while {")]
    [InlineData("repeat { }")]
    [InlineData("match")]
    [InlineData("match x {")]
    [InlineData("match x { case")]
    [InlineData("match x { case .A")]
    [InlineData("match x { case .A =>")]
    [InlineData("switch x { case")]
    [InlineData("do {")]
    [InlineData("do { } catch")]
    [InlineData("region")]
    [InlineData("defer")]
    [InlineData("return return return")]
    [InlineData("{ } } } }")]
    [InlineData("xs.map { a,")]
    [InlineData("[weak self]")]
    [InlineData("outer:")]
    [InlineData("let { a,")]
    public void MalformedStatementsStillRoundTrip(string source)
    {
        var tree = SyntaxTree.ParseStatement(source);
        AssertRoundTrips(tree, source);
    }

    [Fact]
    public void RandomStatementSoupTerminatesAndRoundTrips()
    {
        string[] pieces =
        [
            "{", "}", "(", ")", "[", "]", "let", "var", "if", "else", "guard", "while",
            "for", "in", "match", "case", "default", "=>", "return", "break", "defer",
            "do", "catch", "region", "a", "1", "\"s\"", "+", ".", ",", ":", ";", "_",
            "where", "is", "as", "...", "|", "self", "try", "using", "switch",
        ];

        var random = new Random(20261118);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var builder = new StringBuilder();
            var length = random.Next(1, 16);
            for (var i = 0; i < length; i++)
                builder.Append(pieces[random.Next(pieces.Length)]).Append(' ');

            var source = builder.ToString();
            AssertRoundTrips(SyntaxTree.ParseStatement(source), source);
        }
    }

    [Fact]
    public void DeeplyNestedBlocksDoNotHang()
    {
        var source = new string('{', 150) + new string('}', 150);
        var tree = SyntaxTree.ParseStatement(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        AssertRoundTrips(tree, source);
    }

    [Fact]
    public void StatementNodeSpansTileTheirParent()
    {
        const string source = "{\n    let x = 1\n    for i in 0..<x where i > 0 {\n"
            + "        xs.filter { $0 > i }\n    }\n"
            + "    match x { case 1 => a() default => b() }\n}";

        var tree = SyntaxTree.ParseStatement(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        AssertTiles(tree.Root);
    }

    [Fact]
    public void AMissingTokenIsInsertedWithZeroWidth()
    {
        var tree = SyntaxTree.ParseExpression("f(1");
        Assert.True(tree.HasErrors);

        var missing = tree.Root.DescendantTokens().Single(t => t.IsMissing);
        Assert.Equal(SyntaxKind.CloseParenToken, missing.Kind);
        Assert.Equal(0, missing.Width);
        Assert.Equal(string.Empty, missing.Text);
    }

    [Fact]
    public void SkippedTokensBecomeTriviaOnTheFollowingToken()
    {
        const string source = "1 2 3";
        var tree = SyntaxTree.ParseExpression(source);
        AssertRoundTrips(tree, source);

        var skipped = tree.EndOfFileToken.LeadingTrivia
            .Where(t => t.Kind == SyntaxKind.SkippedTokensTrivia)
            .ToList();
        Assert.NotEmpty(skipped);
        // The space after '1' is that token's trailing trivia, so the skipped
        // text starts at '2'.
        Assert.Equal("2 3", string.Concat(skipped.Select(t => t.Text)));
    }

    [Fact]
    public void DiagnosticsPointAtTheOffendingToken()
    {
        var tree = SyntaxTree.ParseExpression("1 + * 2");
        var diagnostic = tree.Diagnostics.First(d => d.Code == ErrorCode.ERR_ExpectedExpression);
        Assert.Equal(new TextSpan(4, 1), diagnostic.Span);
        Assert.Contains("1:5", diagnostic.Format(tree.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void LexicalDiagnosticsSurviveIntoTheParse()
    {
        var tree = SyntaxTree.ParseExpression("'ab'");
        Assert.Contains(tree.Diagnostics, d => d.Code == ErrorCode.ERR_CharLiteralTooLong);
    }

    [Fact]
    public void ValidInputProducesNoDiagnostics()
    {
        var tree = SyntaxTree.ParseExpression("a.b(c, d: 1)[2] ?? e");
        Assert.Empty(tree.Diagnostics);
    }

    // ---- Tree invariants ---------------------------------------------------

    [Theory]
    [InlineData("a.b(c, d: 1)[2] ?? e")]
    [InlineData("  (1 + 2) * [3, 4]  // trailing\n")]
    [InlineData("Point { x: 1, y: 2 }")]
    [InlineData("x as? List<List<Int>> ?? fallback")]
    public void NodeSpansTileTheirParentWithoutGaps(string source)
    {
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        AssertTiles(tree.Root);
    }

    private static void AssertTiles(SyntaxNode node)
    {
        var expected = node.FullSpan.Start;
        foreach (var child in node.ChildNodesAndTokens())
        {
            Assert.True(expected == child.FullSpan.Start,
                $"{node.Kind}: expected child at {expected} but found {child.FullSpan.Start}");
            expected = child.FullSpan.End;

            if (child.AsNode() is { } childNode) AssertTiles(childNode);
        }
        Assert.Equal(node.FullSpan.End, expected);
    }

    [Fact]
    public void ParentPointersAreConsistent()
    {
        var tree = SyntaxTree.ParseExpression("f(a + b, c.d)");
        foreach (var node in tree.Root.DescendantNodes())
            Assert.NotNull(node.Parent);

        Assert.Null(tree.Root.Parent);
    }

    [Fact]
    public void RedNodesAreCachedSoIdentityIsStable()
    {
        var tree = SyntaxTree.ParseExpression("a + b");
        var binary = Assert.IsType<BinaryExpressionSyntax>(tree.Root);
        Assert.Same(binary.Left, binary.Left);
        Assert.Same(binary.Right, binary.Right);
    }

    [Fact]
    public void VisitorReachesEveryNode()
    {
        var tree = SyntaxTree.ParseExpression("f(a + b, c.d)");
        var counter = new CountingVisitor();
        counter.Visit(tree.Root);
        Assert.Equal(tree.Root.DescendantNodesAndSelf().Count(), counter.Count);
    }

    private sealed class CountingVisitor : SyntaxVisitor
    {
        public int Count { get; private set; }

        public override void DefaultVisit(SyntaxNode node)
        {
            Count++;
            base.DefaultVisit(node);
        }
    }

    // ---- Pathological input -------------------------------------------------

    [Fact]
    public void DeeplyNestedInputDoesNotHang()
    {
        var source = new string('(', 200) + "1" + new string(')', 200);
        var tree = SyntaxTree.ParseExpression(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        AssertRoundTrips(tree, source);
    }

    [Fact]
    public void RandomTokenSoupTerminatesAndRoundTrips()
    {
        string[] pieces =
        [
            "a", "1", "\"s\"", "+", "-", "*", "(", ")", "[", "]", "{", "}", ",", ":",
            ".", "?", "??", "<", ">", "as", "is", "try", "await", "&", "|", "=>",
            "..<", "...", "!", "#", "@", "some", "any", "ref", "self",
        ];

        var random = new Random(20261117);
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var builder = new StringBuilder();
            var length = random.Next(1, 14);
            for (var i = 0; i < length; i++)
                builder.Append(pieces[random.Next(pieces.Length)]).Append(' ');

            var source = builder.ToString();

            var expression = SyntaxTree.ParseExpression(source);
            AssertRoundTrips(expression, source);

            var type = SyntaxTree.ParseType(source);
            AssertRoundTrips(type, source);
        }
    }
}
