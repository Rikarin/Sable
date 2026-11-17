namespace Sable.Symbols.Tests;

public sealed class PatternTests {
    const string Shapes = """
                          union Shape {
                              Circle(radius: F64)
                              Rect(width: F64, height: F64)
                              Empty
                          }
                          enum Direction { North, South, East, West }
                          """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string body, string extra = Shapes) {
        var source = TestSource.Probe(body, extra);
        var compilation = Compilation.Create(source);
        return [
            ..compilation.GetAllDiagnostics()
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
        ];
    }

    static void AssertClean(string body, string extra = Shapes) {
        var errors = ErrorsIn(body, extra);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message} {e.Span}"))
        );
    }

    static string TypeOfMatch(string body, string extra = Shapes) {
        var source = TestSource.Probe(body, extra);
        var compilation = Compilation.Create(source);

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => e.Message)));

        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);
        var match = tree.Root.DescendantNodes().OfType<MatchExpressionSyntax>().First();
        return model.GetTypeOf(match)!.ToDisplayString();
    }

    // ---- Pattern forms -----------------------------------------------------

    [Fact]
    public void BindsUnionCasePatternsAndTheirPayloads() =>
        AssertClean(
            """
            let s: Shape
            match s {
                case .Circle(let r) => { let x: F64 = r }
                case .Rect(let w, let h) => { let y: F64 = w + h }
                case .Empty => { }
            }
            """
        );

    [Fact]
    public void BindsWildcardAndBindingPatterns() =>
        AssertClean(
            """
            let s: Shape
            match s {
                case .Circle(_) => { }
                default => { }
            }
            """
        );

    [Fact]
    public void BindsLiteralAndRangePatterns() =>
        AssertClean(
            """
            let n = 5
            match n {
                case 0 => { }
                case 1...9 => { }
                case -1 => { }
                default => { }
            }
            """
        );

    [Fact]
    public void BindsTuplePatterns() =>
        AssertClean(
            """
            let p = (1, "s")
            match p {
                case (0, _) => { }
                case (let a, let b) => { let x: Int = a
                    let y: String = b }
            }
            """
        );

    [Fact]
    public void BindsOrPatterns() =>
        AssertClean(
            """
            let s: Shape
            match s {
                case .Circle(_) | .Empty => { }
                case .Rect(_, _) => { }
            }
            """
        );

    [Fact]
    public void BindsTypeTestPatterns() =>
        AssertClean(
            """
            let s: Shape
            match s {
                case is Shape => { }
                default => { }
            }
            """
        );

    [Fact]
    public void BindsRecordPatterns() =>
        AssertClean(
            """
            let p: Point
            match p {
                case Point { x: 0, y: let y } => { let a: Int = y }
                default => { }
            }
            """,
            "struct Point { var x: Int\n var y: Int }"
        );

    [Fact]
    public void BindsArrayPatterns() =>
        AssertClean(
            """
            let xs = [1, 2, 3]
            match xs {
                case [let first, ...let rest] => { let a: Int = first }
                default => { }
            }
            """
        );

    [Fact]
    public void BindsEnumCasePatterns() =>
        AssertClean(
            """
            let d: Direction
            match d {
                case .North => { }
                case .South => { }
                case .East => { }
                case .West => { }
            }
            """
        );

    // ---- Errors in patterns -------------------------------------------------

    [Fact]
    public void AnUnknownCaseIsReported() =>
        Assert.Contains(
            ErrorsIn(
                """
                let s: Shape
                match s {
                    case .Triangle => { }
                    default => { }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_UnknownUnionCase
        );

    [Fact]
    public void TheWrongPayloadCountIsReported() =>
        Assert.Contains(
            ErrorsIn(
                """
                let s: Shape
                match s {
                    case .Rect(let w) => { }
                    default => { }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_WrongPayloadCount
        );

    [Fact]
    public void AMistypedLiteralPatternIsReported() =>
        Assert.Contains(
            ErrorsIn(
                """
                let s: Shape
                match s {
                    case 1 => { }
                    default => { }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_PatternTypeMismatch
        );

    // ---- Exhaustiveness -----------------------------------------------------

    [Fact]
    public void AMissingUnionCaseIsReported() {
        var error = Assert.Single(
            ErrorsIn(
                """
                let s: Shape
                match s {
                    case .Circle(_) => { }
                    case .Rect(_, _) => { }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_MatchNotExhaustive
        );

        Assert.Contains("Empty", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CoveringEveryCaseIsExhaustive() =>
        AssertClean(
            """
            let s: Shape
            match s {
                case .Circle(_) => { }
                case .Rect(_, _) => { }
                case .Empty => { }
            }
            """
        );

    [Fact]
    public void ADefaultMakesAMatchExhaustive() =>
        AssertClean(
            """
            let s: Shape
            match s {
                case .Circle(_) => { }
                default => { }
            }
            """
        );

    [Fact]
    public void AGuardedArmDoesNotCount() =>
        Assert.Contains(
            ErrorsIn(
                """
                let s: Shape
                match s {
                    case .Circle(_) => { }
                    case .Rect(_, _) => { }
                    case .Empty where true => { }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_MatchNotExhaustive
        );

    [Fact]
    public void AMissingEnumCaseIsReported() {
        var error = Assert.Single(
            ErrorsIn(
                """
                let d: Direction
                match d {
                    case .North => { }
                    case .South => { }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_MatchNotExhaustive
        );

        Assert.Contains("East", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BooleansAreExhaustedByBothValues() {
        AssertClean(
            """
            let b = true
            match b {
                case true => { }
                case false => { }
            }
            """
        );

        Assert.Contains(
            ErrorsIn(
                """
                let b = true
                match b {
                    case true => { }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_MatchNotExhaustive
        );
    }

    [Fact]
    public void MatchingAnOpenTypeNeedsADefault() =>
        Assert.Contains(
            ErrorsIn(
                """
                let n = 1
                match n {
                    case 0 => { }
                    case 1 => { }
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_MatchNotExhaustive
        );

    // ---- match as an expression ---------------------------------------------

    [Fact]
    public void MatchHasTheTypeOfItsArms() =>
        Assert.Equal(
            "F64",
            TypeOfMatch(
                """
                let s: Shape
                let area = match s {
                    case .Circle(let r) => r
                    case .Rect(let w, let h) => w * h
                    case .Empty => 0.0
                }
                """
            )
        );

    [Fact]
    public void ArmsWithDifferentTypesAreReported() =>
        Assert.Contains(
            ErrorsIn(
                """
                let s: Shape
                let v = match s {
                    case .Circle(_) => 1
                    case .Rect(_, _) => "s"
                    case .Empty => 0
                }
                """
            ),
            static e => e.Code == ErrorCode.ERR_MatchArmTypesDiffer
        );

    [Fact]
    public void BindsIfCasePatterns() =>
        AssertClean(
            """
            let s: Shape
            if case .Circle(let r) = s {
                let x: F64 = r
            }
            """
        );

    // ---- Flow ----------------------------------------------------------------

    [Fact]
    public void NotAllPathsReturningIsReported() {
        var compilation = Compilation.Create(
            """
            module M
            fn f(_ b: Bool) -> Int {
                if b {
                    return 1
                }
            }
            """
        );

        Assert.Contains(
            compilation.GetAllDiagnostics(),
            static e => e.Code == ErrorCode.ERR_NotAllPathsReturn
        );
    }

    [Fact]
    public void BothBranchesReturningIsEnough() {
        var compilation = Compilation.Create(
            """
            module M
            fn f(_ b: Bool) -> Int {
                if b {
                    return 1
                } else {
                    return 2
                }
            }
            """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static e => e.Code == ErrorCode.ERR_NotAllPathsReturn
        );
    }

    [Fact]
    public void AThrowCountsAsAnExit() {
        var compilation = Compilation.Create(
            """
            module M
            union E { Bad }
            fn f(_ b: Bool) -> Int {
                if b {
                    return 1
                } else {
                    throw E.Bad
                }
            }
            """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static e => e.Code == ErrorCode.ERR_NotAllPathsReturn
        );
    }

    [Fact]
    public void AnExhaustiveMatchCountsAsAnExit() {
        var compilation = Compilation.Create(
            $$"""
              module M
              {{Shapes}}
              fn f(_ s: Shape) -> Int {
                  match s {
                      case .Circle(_) => { return 1 }
                      case .Rect(_, _) => { return 2 }
                      case .Empty => { return 3 }
                  }
              }
              """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static e => e.Code == ErrorCode.ERR_NotAllPathsReturn
        );
    }

    [Fact]
    public void WhileTrueCountsAsAnExit() {
        var compilation = Compilation.Create(
            """
            module M
            fn f() -> Int {
                while true {
                    let x = 1
                }
            }
            """
        );

        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static e => e.Code == ErrorCode.ERR_NotAllPathsReturn
        );
    }

    [Fact]
    public void WhileTrueWithABreakDoesNotCount() {
        var compilation = Compilation.Create(
            """
            module M
            fn f() -> Int {
                while true {
                    break
                }
            }
            """
        );

        Assert.Contains(
            compilation.GetAllDiagnostics(),
            static e => e.Code == ErrorCode.ERR_NotAllPathsReturn
        );
    }

    [Fact]
    public void UnreachableCodeIsWarned() {
        var compilation = Compilation.Create(
            """
            module M
            fn f() -> Int {
                return 1
                let x = 2
            }
            """
        );

        Assert.Contains(
            compilation.GetAllDiagnostics(),
            static e => e.Code == ErrorCode.WRN_UnreachableCode
        );
    }
}
