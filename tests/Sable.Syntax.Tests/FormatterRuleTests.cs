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

    [Theory]
    [InlineData("let s = if x > 3 { \"big\" } else { \"small\" }")]
    [InlineData("let p = f(if a { 1 } else if b { 2 } else { 3 })")]
    [InlineData("let n = both { 1 } onFail: { 2 }")]
    [InlineData("let a = match o {\n    case let v? => v\n    default     => 0\n}")]
    public void AnIfThatGivesAValueStaysWhereTheValueGoes(string statement) => AssertFormats(statement, statement + "\n");

    [Fact]
    public void AnIfValueWrittenOnSeveralLinesKeepsThem() =>
        AssertFormats(
            "let s = if x > 3 {\n    let y = x\n    y\n} else {\n    0\n}\n",
            "let s = if x > 3 {\n    let y = x\n    y\n} else {\n    0\n}\n");

    // A colon-form case's statements are indented from its 'case', as a
    // block's are from its '{'; they were left at the case's own level.
    [Theory]
    [InlineData("match")]
    [InlineData("switch")]
    public void AColonCaseIndentsItsStatements(string keyword) =>
        AssertFormats(
            $"fn f(_ n: Int) -> Int {{\n    {keyword} n {{\n        case 0:\n        return 0\n        case 1: // one\n        let m = n\n"
            + $"        return m\n        // the rest\n        default:\n        if n < 0 {{\n        return -1\n        }}\n        return 2\n    }}\n}}\n",
            $"fn f(_ n: Int) -> Int {{\n    {keyword} n {{\n        case 0:\n            return 0\n        case 1: // one\n            let m = n\n"
            + $"            return m\n        // the rest\n        default:\n            if n < 0 {{\n                return -1\n            }}\n"
            + $"            return 2\n    }}\n}}\n");

    [Fact]
    public void AOneStatementCaseWrittenOnItsLineStaysThere() =>
        AssertFormats(
            "fn f(_ s: Int) {\n    switch s {\n        case 200, 201: handleOk()\n        case 404:\n        missing()\n        default: break\n    }\n}\n",
            "fn f(_ s: Int) {\n    switch s {\n        case 200, 201: handleOk()\n        case 404:\n            missing()\n        default: break\n    }\n}\n");

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

    [Theory]
    [InlineData("struct Box<T: ~Copyable> { }\n")]
    [InlineData("fn f<T>(_ x: T) where T: Named & ~Copyable { }\n")]
    [InlineData("struct Buf<T, N> where N > 0, N % 2 == 0 { }\n")]
    public void ConstraintsKeepTheirShape(string source) => AssertFormats(source, source);

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
    // …and so does an 'if'/'else' chain of such bodies, written on one line (#203)…
    [InlineData("fn f() {\n    if a { return }\n}\n", "fn f() {\n    if a { return }\n}\n")]
    [InlineData("fn f() {\n    if a { return 1 } else if b { return 2 } else { return 3 }\n}\n",
        "fn f() {\n    if a { return 1 } else if b { return 2 } else { return 3 }\n}\n")]
    // …but a loop's block takes its own lines, and so does a body written over several,
    // a chain only part of which is on one line, and a body that holds an 'if'.
    [InlineData("fn f() {\n    while a { g() }\n}\n", "fn f() {\n    while a {\n        g()\n    }\n}\n")]
    [InlineData("fn f() {\n    return\n}\n", "fn f() {\n    return\n}\n")]
    [InlineData("fn f() {\n    if a { g() } else {\n        h()\n    }\n}\n", "fn f() {\n    if a {\n        g()\n    } else {\n        h()\n    }\n}\n")]
    [InlineData("fn f() { if a { return } }\n", "fn f() {\n    if a { return }\n}\n")]
    public void OneLineBodies(string source, string expected) => AssertFormats(source, expected);

    [Theory]
    // A type's body written on one line, of members without bodies of their own, stays there (#203)…
    [InlineData("struct P { var x: Int }\n", "struct P { var x: Int }\n")]
    [InlineData("struct Q {var x: Int;var y: Int}\n", "struct Q { var x: Int; var y: Int }\n")]
    [InlineData("enum Dir { north, south }\n", "enum Dir { north, south }\n")]
    [InlineData("union U { A, B(x: Int) }\n", "union U { A, B(x: Int) }\n")]
    [InlineData("protocol Shape { fn area() -> F64 }\n", "protocol Shape { fn area() -> F64 }\n")]
    [InlineData("extension P { fn g() -> Int { return x } }\n", "extension P { fn g() -> Int { return x } }\n")]
    [InlineData("/// A point.\n@derive(Eq)\npublic struct P { var x: Int }\n", "/// A point.\n@derive(Eq)\npublic struct P { var x: Int }\n")]
    // …and one written over several lines, or with a member that has a body, does not.
    [InlineData("enum Dir {\n    north, south\n}\n", "enum Dir {\n    north,\n    south\n}\n")]
    [InlineData("struct S { fn f() {\n    g()\n} }\n", "struct S {\n    fn f() {\n        g()\n    }\n}\n")]
    [InlineData("struct S { struct T { var x: Int } }\n", "struct S {\n    struct T { var x: Int }\n}\n")]
    public void OneLineTypes(string source, string expected) => AssertFormats(source, expected);

    [Theory]
    // A closure with a header around one expression, written on one line, stays there (#203).
    [InlineData("fn f() {\n    let ys = xs.map { x in x * 2 }\n}\n", "fn f() {\n    let ys = xs.map { x in x * 2 }\n}\n")]
    [InlineData("fn f() {\n    let ys = xs.map { (x: Int) -> Int in x * 2 }\n}\n", "fn f() {\n    let ys = xs.map { (x: Int) -> Int in x * 2 }\n}\n")]
    [InlineData("fn f() {\n    let ys = xs.map { x in\n        x * 2 }\n}\n", "fn f() {\n    let ys = xs.map { x in\n        x * 2\n    }\n}\n")]
    [InlineData("fn f() {\n    let ys = xs.map { x in return x * 2 }\n}\n", "fn f() {\n    let ys = xs.map { x in\n        return x * 2\n    }\n}\n")]
    public void OneLineClosures(string source, string expected) => AssertFormats(source, expected);

    [Fact]
    public void LinesUpTheArrowsOfARunOfArms() =>
        AssertFormats("""
                      fn f() -> Int {
                          return match d {
                              case .North => 1
                              case .East => 2
                              case .Nowhere(let reason) =>
                                  3
                              case .Up => 4
                              case .Down => 5

                              // after a blank line, a new run
                              case .Sideways => 6
                              default => 7
                          }
                      }

                      """, """
                           fn f() -> Int {
                               return match d {
                                   case .North => 1
                                   case .East  => 2
                                   case .Nowhere(let reason) =>
                                       3
                                   case .Up   => 4
                                   case .Down => 5

                                   // after a blank line, a new run
                                   case .Sideways => 6
                                   default        => 7
                               }
                           }

                           """);

    [Fact]
    public void LinesUpANestedMatchOnItsOwn() =>
        AssertFormats("""
                      fn f() -> Int {
                          return match a {
                              case 1 => match b {
                                  case 10 => 1
                                  case 2 => 2
                              }
                              case 100 => 3
                              default => 4
                          }
                      }

                      """, """
                           fn f() -> Int {
                               return match a {
                                   case 1 => match b {
                                       case 10 => 1
                                       case 2  => 2
                                   }
                                   case 100 => 3
                                   default  => 4
                               }
                           }

                           """);

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
