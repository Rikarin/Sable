namespace Sable.Symbols.Tests;

/// <summary>
///     <c>switch</c>, the C-shaped dispatch of SYNTAX.md §15: where
///     <c>fallthrough</c> and <c>break</c> may go, and what flow analysis makes
///     of a case that falls through.
/// </summary>
public sealed class SwitchTests {
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
    public void FallthroughGoesOnToTheNextCase() =>
        AssertClean(
            """
            fn f(_ n: Int) {
                switch n {
                    case 1:
                        if n > 0 {
                            fallthrough
                        }
                    case 2: return
                    default: return
                }
            }
            """
        );

    [Fact]
    public void FallthroughInTheLastCaseIsAnError() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ n: Int) {
                    switch n {
                        case 1: return
                        default: fallthrough
                    }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_FallthroughOutOfPlace
        );

    [Fact]
    public void FallthroughOutsideASwitchIsAnError() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ n: Int) {
                    fallthrough
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_FallthroughOutOfPlace
        );

    [Fact]
    public void AClosureInACaseCannotFallThrough() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ n: Int) {
                    switch n {
                        case 1:
                            let g = () => {
                                fallthrough
                            }
                        default: return
                    }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_FallthroughOutOfPlace
        );

    [Fact]
    public void BreakLeavesASwitchOutsideAnyLoop() =>
        AssertClean(
            """
            fn f(_ n: Int) {
                switch n {
                    case 1: break
                    default: return
                }
            }
            """
        );

    [Fact]
    public void ContinueStillNeedsALoop() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ n: Int) {
                    switch n {
                        case 1: continue
                        default: return
                    }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_NoEnclosingLoop
        );

    [Fact]
    public void EveryCaseAndADefaultAssignAVariable() =>
        AssertClean(
            """
            fn f(_ n: Int) -> Int {
                var x: Int
                switch n {
                    case 1:
                        x = 1
                        fallthrough
                    case 2:
                        x = 2
                    default:
                        x = 0
                }
                return x
            }
            """
        );

    [Fact]
    public void WithoutADefaultNoCaseMayRun() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ n: Int) -> Int {
                    var x: Int
                    switch n {
                        case 1: x = 1
                        case 2: x = 2
                    }
                    return x
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_UseBeforeAssignment
        );

    [Fact]
    public void ACaseReachedDirectlyMayNotHaveIt() =>
        Assert.Contains(
            ErrorsIn(
                """
                fn f(_ n: Int) -> Int {
                    var x: Int
                    switch n {
                        case 1:
                            x = 1
                            fallthrough
                        case 2: return x
                        default: return 0
                    }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_UseBeforeAssignment
        );
}
