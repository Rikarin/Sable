namespace Sable.Symbols.Tests;

/// <summary>
///     Flow-sensitive narrowing, per docs/03 §3.5: a test in scope gives a
///     binding a narrower type for as long as the test holds.
/// </summary>
public sealed class NarrowingTests {
    const string Shapes = """
                          protocol Shape { fn area() -> F64 }
                          struct Circle: Shape {
                              var radius: F64
                              fn area() -> F64 { return radius }
                              fn describe() -> String { return "circle" }
                          }
                          """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Shapes}\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    // ---- 'is' --------------------------------------------------------------

    [Fact]
    public void AnIsTestNarrowsTheBranch() =>
        AssertClean(
            """
            fn probe(_ s: any Shape) -> String {
                if s is Circle {
                    return s.describe()
                }
                return ""
            }
            """
        );

    [Fact]
    public void TheNarrowingEndsWithTheBranch() {
        var errors = ErrorsIn(
            """
            fn probe(_ s: any Shape) -> String {
                if s is Circle {
                    return s.describe()
                }
                return s.describe()
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    [Fact]
    public void TheElseBranchIsNotNarrowed() {
        var errors = ErrorsIn(
            """
            fn probe(_ s: any Shape) -> String {
                if s is Circle {
                    return "c"
                } else {
                    return s.describe()
                }
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    [Fact]
    public void NotIsNarrowsTheElse() =>
        AssertClean(
            """
            fn probe(_ s: any Shape) -> String {
                if !(s is Circle) {
                    return ""
                } else {
                    return s.describe()
                }
            }
            """
        );

    [Fact]
    public void BothSidesOfAndNarrow() =>
        AssertClean(
            """
            fn probe(_ s: any Shape, _ flag: Bool) -> String {
                if s is Circle && flag {
                    return s.describe()
                }
                return ""
            }
            """
        );

    // ---- optionals ---------------------------------------------------------

    [Fact]
    public void ANoneComparisonUnwraps() =>
        AssertClean(
            """
            fn probe(_ n: Int?) -> Int {
                if n != none {
                    return n
                }
                return 0
            }
            """
        );

    [Fact]
    public void AnEqualsNoneComparisonUnwrapsTheElse() =>
        AssertClean(
            """
            fn probe(_ n: Int?) -> Int {
                if n == none {
                    return 0
                } else {
                    return n
                }
            }
            """
        );

    [Fact]
    public void AGuardNarrowsWhatFollows() =>
        AssertClean(
            """
            fn probe(_ s: any Shape) -> String {
                guard s is Circle else {
                    return ""
                }
                return s.describe()
            }
            """
        );

    [Fact]
    public void AGuardOnNoneUnwraps() =>
        AssertClean(
            """
            fn probe(_ n: Int?) -> Int {
                guard n != none else {
                    return 0
                }
                return n
            }
            """
        );

    // ---- what ends it ------------------------------------------------------

    [Fact]
    public void AssigningToTheBindingEndsIt() {
        var errors = ErrorsIn(
            """
            fn probe(_ start: any Shape) -> String {
                var s = start
                if s is Circle {
                    s = start
                    return s.describe()
                }
                return ""
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    [Fact]
    public void AnUnrelatedConditionNarrowsNothing() {
        var errors = ErrorsIn(
            """
            fn probe(_ s: any Shape, _ flag: Bool) -> String {
                if flag {
                    return s.describe()
                }
                return ""
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    // ---- writes and closures ------------------------------------------------

    [Fact]
    public void AnAssignmentInsideTheNarrowedBranchMayStoreAnything() =>
        AssertClean(
            """
            fn probe(_ start: Circle?) {
                var c = start
                if c != none {
                    c = none
                }
            }
            """
        );

    [Fact]
    public void ACallToAClosureThatWritesTheBindingEndsTheNarrowing() {
        var errors = ErrorsIn(
            """
            fn probe(_ start: Circle?) -> F64 {
                var c = start
                let clear = () => { c = none }
                if c != none {
                    clear()
                    return c.radius
                }
                return 0
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotOptional);
    }

    [Fact]
    public void ANarrowingStillHoldsWhenNoClosureWritesTheBinding() =>
        AssertClean(
            """
            fn probe(_ start: Circle?) -> F64 {
                var c = start
                let other = () => { }
                if c != none {
                    other()
                    return c.radius
                }
                return 0
            }
            """
        );

    [Fact]
    public void AClosureDoesNotSeeAVarNarrowedOutsideIt() {
        // The closure runs later, when 'c' may be none again.
        var errors = ErrorsIn(
            """
            fn probe(_ start: Circle?) -> () -> F64 {
                var c = start
                if c != none {
                    let f = () => c.radius
                    c = none
                    return f
                }
                return () => 0
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotOptional);
    }

    [Fact]
    public void AClosureDoesSeeALetNarrowedOutsideIt() =>
        AssertClean(
            """
            fn probe(_ c: Circle?) -> () -> F64 {
                if c != none {
                    return () => c.radius
                }
                return () => 0
            }
            """
        );
}
