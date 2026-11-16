namespace Sable.Syntax.Tests;

/// <summary>
/// The newline rule of spec/lexical.md §4. These tests are the executable
/// specification of where statements end; if a rule changes, change it here
/// and in the spec together.
/// </summary>
public sealed class StatementTerminatorTests
{
    /// <summary>Returns the text of each token after which a statement ends.</summary>
    private static IReadOnlyList<string> Terminators(string source) =>
        [.. Lexer.Tokenize(new SourceText(source), out _)
            .Where(t => t.TerminatesStatement && t.Kind != SyntaxKind.EndOfFile)
            .Select(t => t.Text)];

    [Fact]
    public void NewlineEndsACompleteStatement()
    {
        Assert.Equal(["1", "2"], Terminators("let x = 1\nlet y = 2\n"));
    }

    [Fact]
    public void SemicolonAlwaysTerminates()
    {
        Assert.Equal([";", ";"], Terminators("let x = 1; let y = 2;"));
    }

    [Fact]
    public void TrailingBinaryOperatorContinuesTheLine()
    {
        Assert.Empty(Terminators("let x = 1 +\n    2"));
        Assert.Empty(Terminators("let x = a &&\n    b"));
        Assert.Empty(Terminators("let x = a ??\n    b"));
    }

    [Fact]
    public void LeadingDotContinuesTheLine()
    {
        Assert.Empty(Terminators("let x = foo\n    .bar()\n    .baz()"));
    }

    [Fact]
    public void LeadingBinaryOperatorContinuesTheLine()
    {
        Assert.Empty(Terminators("let x = a\n    + b"));
    }

    [Fact]
    public void NewlinesInsideParenthesesNeverTerminate()
    {
        Assert.Empty(Terminators("foo(\n    1,\n    2\n)"));
    }

    [Fact]
    public void NewlinesInsideBracketsNeverTerminate()
    {
        Assert.Empty(Terminators("let xs = [\n    1,\n    2\n]"));
    }

    [Fact]
    public void BracesDoNotSuppressTermination()
    {
        // A statement inside a block still ends at its own newline.
        Assert.Equal(["1"], Terminators("{\n    let x = 1\n}"));
    }

    [Fact]
    public void ElseAndCatchAndWhileContinueTheLine()
    {
        Assert.Empty(Terminators("if a { }\nelse { }"));
        Assert.Empty(Terminators("do { }\ncatch { }"));
        Assert.Empty(Terminators("repeat { }\nwhile x"));
    }

    [Fact]
    public void WhereContinuesTheLine()
    {
        Assert.Empty(Terminators("fn f<T>(x: T) -> T\n    where T: Comparable"));
    }

    [Fact]
    public void BareReturnTerminatesUnconditionally()
    {
        // The guard against JavaScript's ASI hazard: 'return' must not swallow
        // the next line even though it opens with a continuation token.
        Assert.Equal(["return"], Terminators("return\n    .foo()"));
    }

    [Theory]
    [InlineData("break")]
    [InlineData("continue")]
    [InlineData("fallthrough")]
    public void JumpKeywordsTerminateUnconditionally(string keyword)
    {
        Assert.Equal([keyword], Terminators(keyword + "\n    + 1"));
    }

    [Fact]
    public void ReturnWithAValueTerminatesAtTheValue()
    {
        Assert.Equal(["x"], Terminators("return x\nlet y = 1").Take(1).ToList());
    }

    [Fact]
    public void GenericTypeClosingAngleCanEndAStatement()
    {
        Assert.Equal([">"], Terminators("var xs: List<Int>\nvar y = 1").Take(1).ToList());
    }

    [Fact]
    public void OptionalQuestionMarkCanEndAStatement()
    {
        Assert.Equal(["?"], Terminators("var x: Int?\nvar y = 1").Take(1).ToList());
    }

    [Fact]
    public void ClosingBraceEndsAStatement()
    {
        Assert.Equal(["}"], Terminators("fn f() { }\nfn g() { }").Take(1).ToList());
    }

    [Fact]
    public void AnIncompleteLineDoesNotTerminate()
    {
        Assert.Empty(Terminators("let x =\n    1"));
        Assert.Empty(Terminators("fn f(\n)"));
    }

    [Fact]
    public void EndOfFileIsAlwaysAStatementBoundary()
    {
        var tokens = Lexer.Tokenize(new SourceText("let x = 1"), out _);
        Assert.True(tokens[^1].TerminatesStatement);
        Assert.Equal(SyntaxKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void CommentsBetweenLinesDoNotPreventTermination()
    {
        Assert.Equal(["1", "2"], Terminators("let x = 1  // note\nlet y = 2\n"));
    }

    [Fact]
    public void BlankLinesDoNotProduceExtraTerminators()
    {
        Assert.Equal(["1", "2"], Terminators("let x = 1\n\n\nlet y = 2\n"));
    }
}
