namespace Sable.Syntax.Tests;

/// <summary>
///     The layout rules added when the formatter first ran over the whole
///     repository, each with the input that showed it was missing.
/// </summary>
public sealed class FormatterRuleTests {
    static void AssertFormats(string source, string expected) {
        var formatted = SyntaxFormatter.Format(source);
        Assert.Equal(expected, formatted);
        Assert.Equal(formatted, SyntaxFormatter.Format(formatted));
    }

    [Fact]
    public void ElseIfStaysOnTheLineOfTheBrace() =>
        AssertFormats(
            "fn f() {\n    if a {\n        x()\n    } else if b {\n        y()\n    }\n}\n",
            "fn f() {\n    if a {\n        x()\n    } else if b {\n        y()\n    }\n}\n");

    [Fact]
    public void ALabelStaysWithItsLoop() =>
        AssertFormats("fn f() {\n    outer: for r in rows {\n        break outer\n    }\n}\n",
            "fn f() {\n    outer: for r in rows {\n        break outer\n    }\n}\n");

    [Theory]
    [InlineData("let a = try? read(p)")]
    [InlineData("let a = x as? Int")]
    [InlineData("let a = f(x)?.y")]
    [InlineData("let a = xs[0]?.y")]
    public void QuestionMarksSpaceAsTheyRead(string statement) => AssertFormats(statement, statement + "\n");

    [Fact]
    public void ANegativeLiteralPatternKeepsItsSign() =>
        AssertFormats("fn f(_ n: Int) -> Int {\n    return match n {\n        case -1 => 0\n        default => n\n    }\n}\n",
            "fn f(_ n: Int) -> Int {\n    return match n {\n        case -1 => 0\n        default => n\n    }\n}\n");

    [Fact]
    public void AnOperatorNameStandsApartFromItsParameters() =>
        AssertFormats("struct S {\n    public operator ==(lhs: S, rhs: S) -> Bool { return true }\n}\n",
            "struct S {\n    public operator == (lhs: S, rhs: S) -> Bool { return true }\n}\n");

    [Theory]
    // A declaration's one-statement body written on one line stays there…
    [InlineData("struct S {\n    fn hash() -> Int { return 1 }\n}\n", "struct S {\n    fn hash() -> Int { return 1 }\n}\n")]
    [InlineData("struct S {\n    var n: Int { get { return 1 } }\n}\n", "struct S {\n    var n: Int { get { return 1 } }\n}\n")]
    [InlineData("fn f() {\n    guard let x = y else { return }\n}\n", "fn f() {\n    guard let x = y else { return }\n}\n")]
    [InlineData("fn f() -> String {\n    return try read(p) catch { \"\" }\n}\n", "fn f() -> String {\n    return try read(p) catch { \"\" }\n}\n")]
    // …but a statement's block takes its own lines, and so does a body written over several.
    [InlineData("fn f() {\n    if a { return }\n}\n", "fn f() {\n    if a {\n        return\n    }\n}\n")]
    [InlineData("fn f() {\n    return\n}\n", "fn f() {\n    return\n}\n")]
    [InlineData("fn f() { if a { return } }\n", "fn f() {\n    if a {\n        return\n    }\n}\n")]
    public void OneLineBodies(string source, string expected) => AssertFormats(source, expected);

    [Fact]
    public void AClosureHeaderStaysOnTheBracesLine() =>
        AssertFormats("fn f() {\n    let n = reduced(xs, 0) { total, x in\n        total + x\n    }\n}\n",
            "fn f() {\n    let n = reduced(xs, 0) { total, x in\n        total + x\n    }\n}\n");

    [Fact]
    public void KeepsTheAuthorsBreaksInAStructLiteral() {
        const string source = "fn f() -> P {\n    return P {\n        x: 1,\n        y: 2,\n    }\n}\n";
        AssertFormats(source, source);
    }

    [Fact]
    public void KeepsTheAuthorsBreaksInAnArgumentList() {
        const string source = "fn f() {\n    g(\n        1,\n        2\n    )\n}\n";
        AssertFormats(source, source);
    }

    [Fact]
    public void IndentsAContinuationOnce() =>
        AssertFormats("fn f() {\n    let s = cond\n    ? a\n            : b\n    h()\n}\n",
            "fn f() {\n    let s = cond\n        ? a\n        : b\n    h()\n}\n");

    [Fact]
    public void AContinuedConditionDoesNotIndentTheBody() =>
        AssertFormats("fn f() {\n    if a &&\n        b {\n        g()\n    }\n}\n",
            "fn f() {\n    if a &&\n        b {\n        g()\n    }\n}\n");

    [Fact]
    public void AnInoutArgumentIsSpacedLikeAnyOther() =>
        AssertFormats("fn f() {\n    g(&a,&b, into:&c)\n}\n", "fn f() {\n    g(&a, &b, into: &c)\n}\n");

    [Fact]
    public void KeepsABlankLineAfterAComment() =>
        AssertFormats("module M\n\n// note\n\nfn f() { }\n", "module M\n\n// note\n\nfn f() { }\n");
}
