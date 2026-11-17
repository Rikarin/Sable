namespace Sable.Symbols.Tests;

/// <summary>
///     Exhaustiveness and redundancy by Maranget's usefulness algorithm, per
///     docs/03 §3.3: nested patterns, tuples, optionals and arrays, with a
///     witness for what is missing.
/// </summary>
public sealed class ExhaustivenessTests {
    const string Types = """
                         union Inner { A, B }
                         union Outer { Wrap(Inner), Nothing }
                         union Shape { Circle(Bool), Square }
                         struct Point { var x: Bool, var y: Bool }
                         """;

    static IReadOnlyList<Diagnostic> DiagnosticsIn(string source) => [
        ..Compilation.Create($"module M\n{Types}\n{source}").GetAllDiagnostics()
    ];

    static Diagnostic NotExhaustive(string source) =>
        Assert.Single(DiagnosticsIn(source), static d => d.Code == ErrorCode.ERR_MatchNotExhaustive);

    static void AssertExhaustive(string source) {
        var diagnostics = DiagnosticsIn(source);
        Assert.True(
            diagnostics.Count == 0,
            string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}"))
        );
    }

    // ---- Nested patterns ----------------------------------------------------

    [Fact]
    public void EveryNestedCaseCoversTheOuterOne() =>
        AssertExhaustive(
            """
            fn f(_ o: Outer) -> Int {
                return match o {
                    case .Wrap(.A) => 1
                    case .Wrap(.B) => 2
                    case .Nothing => 3
                }
            }
            """
        );

    [Fact]
    public void AMissingNestedCaseIsNamed() {
        var error = NotExhaustive(
            """
            fn f(_ o: Outer) -> Int {
                return match o {
                    case .Wrap(.A) => 1
                    case .Nothing => 3
                }
            }
            """
        );
        Assert.Contains("'.Wrap(.B)'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BooleanPayloadsCoverACase() =>
        AssertExhaustive(
            """
            fn f(_ s: Shape) -> Int {
                return match s {
                    case .Circle(true) => 1
                    case .Circle(false) => 2
                    case .Square => 3
                }
            }
            """
        );

    // ---- Tuples, records, optionals ----------------------------------------

    [Fact]
    public void ATupleOfBooleansNeedsEveryCombination() {
        var error = NotExhaustive(
            """
            fn f(_ a: Bool, _ b: Bool) -> Int {
                return match (a, b) {
                    case (true, true) => 1
                    case (false, false) => 2
                }
            }
            """
        );
        Assert.Contains("(false, true)", error.Message, StringComparison.Ordinal);
        Assert.Contains("(true, false)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATupleWithWildcardsCanBeComplete() =>
        AssertExhaustive(
            """
            fn f(_ a: Bool, _ b: Bool) -> Int {
                return match (a, b) {
                    case (true, _) => 1
                    case (_, true) => 2
                    case (false, false) => 3
                }
            }
            """
        );

    [Fact]
    public void ARecordPatternIsCoveredFieldByField() {
        var error = NotExhaustive(
            """
            fn f(_ p: Point) -> Int {
                return match p {
                    case Point { x: true } => 1
                    case Point { y: true } => 2
                }
            }
            """
        );
        Assert.Contains("x: false", error.Message, StringComparison.Ordinal);
        Assert.Contains("y: false", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingOnlyNoneLeavesAValue() {
        var error = NotExhaustive(
            """
            fn f(_ a: Int?) -> Int {
                return match a {
                    case none => 0
                }
            }
            """
        );
        Assert.Contains(".Some(_)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteTupleMatchNoLongerHidesAMissingReturn() =>
        // The audit's case: as a statement this used to pass silently.
        NotExhaustive(
            """
            fn f(_ a: Bool, _ b: Bool) -> Int {
                match (a, b) {
                    case (true, true) => { return 1 }
                    case (false, false) => { return 2 }
                }
            }
            """
        );

    // ---- Arrays -------------------------------------------------------------

    [Fact]
    public void ARestPatternDoesNotCoverTheEmptyArray() {
        var error = NotExhaustive(
            """
            fn f(_ xs: Int[]) -> Int {
                return match xs {
                    case [let first, ...] => first
                }
            }
            """
        );
        Assert.Contains("'[]'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyAndNonEmptyCoverEveryArray() =>
        AssertExhaustive(
            """
            fn f(_ xs: Int[]) -> Int {
                return match xs {
                    case [] => 0
                    case [let first, ...] => first
                }
            }
            """
        );

    [Fact]
    public void FixedLengthsLeaveTheLongerOnes() {
        var error = NotExhaustive(
            """
            fn f(_ xs: Int[]) -> Int {
                return match xs {
                    case [] => 0
                    case [_] => 1
                }
            }
            """
        );
        Assert.Contains("[_, _, ...]", error.Message, StringComparison.Ordinal);
    }

    // ---- Infinite types -----------------------------------------------------

    [Fact]
    public void IntegerLiteralsNeedAWildcard() =>
        Assert.Contains(
            "'_'",
            NotExhaustive(
                """
                fn f(_ n: Int) -> Int {
                    return match n {
                        case 0 => 0
                        case 1 => 1
                    }
                }
                """
            ).Message,
            StringComparison.Ordinal
        );

    // ---- Redundancy ---------------------------------------------------------

    [Fact]
    public void ANestedArmCoveredByEarlierOnesIsReported() =>
        Assert.Contains(
            DiagnosticsIn(
                """
                fn f(_ o: Outer) -> Int {
                    return match o {
                        case .Wrap(_) => 1
                        case .Wrap(.A) => 2
                        case .Nothing => 3
                    }
                }
                """
            ),
            static d => d.Code == ErrorCode.WRN_PatternAlreadyCovered
        );

    [Fact]
    public void AGuardedArmCoversNothing() {
        var error = NotExhaustive(
            """
            fn f(_ s: Shape, _ go: Bool) -> Int {
                return match s {
                    case .Circle(_) where go => 1
                    case .Square => 3
                }
            }
            """
        );
        Assert.Contains(".Circle(_)", error.Message, StringComparison.Ordinal);
    }
}
