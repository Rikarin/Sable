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
}
