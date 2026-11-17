namespace Sable.Syntax.Tests;

/// <summary>
///     Deep nesting must not overflow the stack: recursive descent spends stack
///     per level, and a stack overflow ends the process rather than throwing.
///     Past the limit the parser reports once and keeps the text as skipped
///     trivia, so the round-trip still holds.
/// </summary>
public sealed class NestingTests {
    const int Deep = 20_000;

    static void AssertSurvives(SyntaxTree tree, string source) {
        Assert.Equal(source, tree.ToFullString());
        Assert.Single(tree.Diagnostics, static d => d.Code == ErrorCode.ERR_NestedTooDeeply);
    }

    static string Nest(string open, string middle, string close) =>
        string.Concat(Enumerable.Repeat(open, Deep)) + middle + string.Concat(Enumerable.Repeat(close, Deep));

    [Fact]
    public void Parentheses() {
        var source = Nest("(", "1", ")");
        AssertSurvives(SyntaxTree.ParseExpression(source), source);
    }

    [Fact]
    public void Brackets() {
        var source = Nest("[", "1", "]");
        AssertSurvives(SyntaxTree.ParseExpression(source), source);
    }

    [Fact]
    public void PrefixOperators() {
        var source = new string('-', Deep) + "x";
        AssertSurvives(SyntaxTree.ParseExpression(source), source);
    }

    [Fact]
    public void Blocks() {
        var source = Nest("{ ", "", " }");
        AssertSurvives(SyntaxTree.ParseStatement(source), source);
    }

    [Fact]
    public void Types() {
        var source = Nest("List<", "Int", ">");
        AssertSurvives(SyntaxTree.ParseType(source), source);
    }

    [Fact]
    public void Patterns() {
        var source = $"match x {{ case {Nest("(", "_", ")")} => 1 }}";
        AssertSurvives(SyntaxTree.ParseStatement(source), source);
    }

    [Fact]
    public void AWholeFile() {
        var source = $"module M\nfn f() -> Int {{ return {Nest("(", "1", ")")} }}\n";
        AssertSurvives(SyntaxTree.Parse(source), source);
    }

    [Fact]
    public void RealisticNestingIsUntouched() {
        var source = Nest("(", "1", ")")[(Deep - 100)..^(Deep - 100)];
        var tree = SyntaxTree.ParseExpression(source);
        Assert.Equal(source, tree.ToFullString());
        Assert.Empty(tree.Diagnostics);
    }
}
