namespace Sable.Syntax.Tests;

/// <summary>
///     The newline rule of spec/lexical.md §4 as the parser applies it: where
///     the lexer says a statement ends, nothing may extend it, and two
///     statements on one line need a ';'.
/// </summary>
public sealed class NewlineRuleParserTests {
    static BlockSyntax Block(string body, out SyntaxTree tree) {
        var source = "{\n" + body + "\n}";
        tree = SyntaxTree.ParseStatement(source);
        Assert.Equal(source, tree.ToFullString());
        return Assert.IsType<BlockSyntax>(tree.Root);
    }

    static int StatementCount(string body, out SyntaxTree tree) => Block(body, out tree).Statements.Count;

    static void AssertStatements(int expected, string body) {
        var count = StatementCount(body, out var tree);
        Assert.Empty(tree.Diagnostics);
        Assert.Equal(expected, count);
    }

    static void AssertReports(ErrorCode code, string body) {
        Block(body, out var tree);
        Assert.Contains(tree.Diagnostics, d => d.Code == code);
    }

    // ---- A new line starts a new statement --------------------------------

    [Fact]
    public void AParenthesisOnTheNextLineIsNotACall() => AssertStatements(2, "let x = foo\n(1, 2).print()");

    [Fact]
    public void ABracketOnTheNextLineIsNotASubscript() => AssertStatements(2, "let x = arr\n[0, 1].print()");

    [Fact]
    public void ABraceOnTheNextLineIsNotATrailingClosure() => AssertStatements(2, "run(task)\n{ let y = 1 }");

    [Fact]
    public void AMinusOnTheNextLineContinues() => AssertStatements(1, "let x = a\n    - b");

    [Fact]
    public void ADotOnTheNextLineContinues() => AssertStatements(1, "let x = items\n    .filter(f)\n    .count");

    [Fact]
    public void ATrailingOperatorContinues() => AssertStatements(1, "let x = a +\n    b");

    [Fact]
    public void InsideParenthesesNewLinesDoNotEndAnything() => AssertStatements(1, "let x = f(\n    a,\n    b\n)");

    [Fact]
    public void AClosureInsideParenthesesHasStatementsOfItsOwn() {
        var block = Block("let x = run({\n    let a = 1\n    let b = 2\n})", out var tree);
        Assert.Empty(tree.Diagnostics);
        Assert.Single(block.Statements);
    }

    [Fact]
    public void ANestedGenericTypeEndsAStatement() => AssertStatements(2, "var xs: List<List<Int>>\nlet y = 1");

    [Fact]
    public void AnOptionalTypeEndsAStatement() => AssertStatements(2, "var x: Int?\nlet y = 1");

    [Fact]
    public void AWhileLoopAfterAStatementIsALoop() => AssertStatements(2, "var i = 0\nwhile i < 10 { i += 1 }");

    [Fact]
    public void AWhileAfterRepeatContinuesIt() => AssertStatements(1, "repeat { i += 1 }\nwhile i < 10");

    [Fact]
    public void ReturnTakesNothingFromTheNextLine() => AssertStatements(2, "return\nfoo()");

    // ---- Two statements on one line ---------------------------------------

    [Fact]
    public void TwoStatementsOnOneLineNeedASemicolon() =>
        AssertReports(ErrorCode.ERR_ExpectedStatementEnd, "let a = 1 let b = 2");

    [Fact]
    public void ASemicolonSeparatesThem() => AssertStatements(2, "let a = 1; let b = 2");

    [Fact]
    public void AStatementMayFollowABraceOnTheSameLine() => AssertStatements(2, "if a { b() } c()");

    // ---- Allman braces ----------------------------------------------------

    [Fact]
    public void ABodyBraceOnItsOwnLineIsReported() =>
        AssertReports(ErrorCode.ERR_BraceOnNewLine, "if ready\n{\n    go()\n}");

    [Fact]
    public void ABlockOnItsOwnIsNotAnAllmanBrace() => AssertStatements(2, "go()\n{\n    stop()\n}");
}
