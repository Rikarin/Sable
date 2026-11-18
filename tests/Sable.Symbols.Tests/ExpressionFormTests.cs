namespace Sable.Symbols.Tests;

/// <summary>
///     Expression forms of grammar.ebnf §7: tuple elements as assignment
///     targets, and several trailing closures on one call.
/// </summary>
public sealed class ExpressionFormTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void AnElementOfAVariableTupleCanBeAssigned() =>
        AssertClean(
            """
            fn f() -> Int {
                var pair = (1, 2)
                pair.0 = 9
                pair.1 += 1
                return pair.0 + pair.1
            }
            """
        );

    [Fact]
    public void AnElementOfAConstantTupleCannot() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f() -> Int {
                    let pair = (1, 2)
                    pair.0 = 9
                    return pair.0
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_NotAssignable
        );

    [Fact]
    public void LabelledTrailingClosuresMatchTheirParameters() =>
        AssertClean(
            """
            fn both(_ body: () -> Int, onFail: () -> Int) -> Int {
                return body() + onFail()
            }

            fn f() -> Int {
                return both { 1 } onFail: { 2 }
            }
            """
        );

    [Fact]
    public void AWrongLabelOnATrailingClosureIsReported() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn both(_ body: () -> Int, onFail: () -> Int) -> Int {
                    return body() + onFail()
                }

                fn f() -> Int {
                    return both { 1 } otherwise: { 2 }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );

    // ---- 'if' as a value ------------------------------------------------------

    static string TypeOfIf(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var tree = compilation.SyntaxTrees[0];
        var ifExpression = tree.Root.DescendantNodes().OfType<IfExpressionSyntax>().First();
        return compilation.GetSemanticModel(tree).GetTypeOf(ifExpression)!.ToDisplayString();
    }

    [Fact]
    public void AnIfGivesItsBranchesType() =>
        Assert.Equal(
            "String",
            TypeOfIf(
                """
                fn f(_ x: Int) -> String {
                    let s = if x > 3 { "big" } else if x > 1 { "some" } else { "small" }
                    return s
                }
                """
            )
        );

    [Fact]
    public void TheContextSaysWhatTypeAnIfGives() =>
        Assert.Equal(
            "U8?",
            TypeOfIf(
                """
                fn f(_ x: Int) -> U8? {
                    let small: U8? = if x > 3 { 1 } else { none }
                    return small
                }
                """
            )
        );

    [Fact]
    public void ABranchThatLeavesGivesNoType() =>
        AssertClean(
            """
            fn f(_ x: Int?) -> Int {
                let n = if let v = x { v } else { return 0 }
                return n + 1
            }
            """
        );

    [Fact]
    public void AnIfThatGivesAValueNeedsAnElse() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ x: Int) -> Int {
                    let n = if x > 3 { 1 }
                    return n
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_IfValueNeedsElse
        );

    [Fact]
    public void BranchesOfDifferentTypesAreReported() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ x: Int) -> Int {
                    let n = if x > 3 { 1 } else { "one" }
                    return 0
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_BranchTypesDiffer
        );

    [Fact]
    public void ABranchWithNoValueIsReported() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ x: Int) -> Int {
                    let n = if x > 3 { 1 } else { let y = 2 }
                    return n
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_BranchTypesDiffer
        );

    [Fact]
    public void ALocalAssignedInBothBranchesIsAssigned() =>
        AssertClean(
            """
            fn f(_ x: Int) -> Int {
                var seen: Int
                let n = if x > 3 {
                    seen = 1
                    x
                } else {
                    seen = 2
                    0
                }
                return n + seen
            }
            """
        );

    // ---- Methods as values ------------------------------------------------------

    [Fact]
    public void AMethodWithItsReceiverIsAFunction() =>
        AssertClean(
            """
            struct Counter {
                var count: Int
                fn plus(_ n: Int) -> Int { return count + n }
            }

            fn f() -> Int {
                let c = Counter(count: 1)
                let add: (Int) -> Int = c.plus
                let again = c.plus
                return add(2) + again(3)
            }
            """
        );

    [Fact]
    public void AMutatingMethodIsNotAValue() =>
        Assert.Contains(
            ErrorsIn(
                """
                struct Counter {
                    var count: Int
                    mutating fn bump() { count += 1 }
                }

                fn f() {
                    var c = Counter(count: 1)
                    let bump = c.bump
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_NotAValue
        );
}
