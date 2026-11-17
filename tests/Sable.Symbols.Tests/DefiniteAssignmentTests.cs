namespace Sable.Symbols.Tests;

/// <summary>
///     Reading a local before anything writes to it. The rule at a join is
///     intersection — a name counts as assigned only when every path that reaches
///     the read has set it.
/// </summary>
public sealed class DefiniteAssignmentTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string body, string extra = "") {
        var compilation = Compilation.Create(TestSource.Probe(body, extra));
        return [
            ..compilation.GetAllDiagnostics()
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
        ];
    }

    static void AssertClean(string body, string extra = "") {
        var errors = ErrorsIn(body, extra);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    static void AssertReported(string body, string extra = "") =>
        Assert.Contains(
            ErrorsIn(body, extra),
            static e => e.Code == ErrorCode.ERR_UseBeforeAssignment
        );

    // ---- The straight line -------------------------------------------------

    [Fact]
    public void ADeclarationWithAValueIsAssigned() =>
        AssertClean(
            """
            let x = 1
            let y = x
            """
        );

    [Fact]
    public void ADeclarationWithoutAValueIsNot() =>
        AssertReported(
            """
            var x: Int
            let y = x
            """
        );

    [Fact]
    public void AnAssignmentSettlesIt() =>
        AssertClean(
            """
            var x: Int
            x = 1
            let y = x
            """
        );

    [Fact]
    public void AParameterIsAssigned() =>
        AssertClean(
            """
            let v: Int
            let y = v
            """
        );

    // ---- Branches ----------------------------------------------------------

    [Fact]
    public void OneBranchIsNotEnough() =>
        AssertReported(
            """
            var x: Int
            if true {
                x = 1
            }
            let y = x
            """
        );

    [Fact]
    public void BothBranchesAre() =>
        AssertClean(
            """
            var x: Int
            if true {
                x = 1
            } else {
                x = 2
            }
            let y = x
            """
        );

    [Fact]
    public void ABranchThatDoesNotComeBackCounts() =>
        AssertClean(
            """
            var x: Int
            if true {
                x = 1
            } else {
                return
            }
            let y = x
            """
        );

    [Fact]
    public void AMatchAssignsWhenEveryArmDoes() =>
        AssertClean(
            """
            var x: Int
            match 1 {
                case 1 => { x = 1 }
                default => { x = 2 }
            }
            let y = x
            """
        );

    [Fact]
    public void AMatchWithAnArmThatDoesNotAssignIsNotEnough() =>
        AssertReported(
            """
            var x: Int
            match 1 {
                case 1 => { x = 1 }
                default => { }
            }
            let y = x
            """
        );

    // ---- Loops -------------------------------------------------------------

    [Fact]
    public void AWhileBodyMayNotRun() =>
        AssertReported(
            """
            var x: Int
            while false {
                x = 1
            }
            let y = x
            """
        );

    [Fact]
    public void ARepeatBodyAlwaysRuns() =>
        AssertClean(
            """
            var x: Int
            repeat {
                x = 1
            } while false
            let y = x
            """
        );

    [Fact]
    public void AForBodyMayNotRun() =>
        AssertReported(
            """
            var x: Int
            let items: Int[] = [1]
            for i in items {
                x = i
            }
            let y = x
            """
        );

    [Fact]
    public void TheLoopElementIsAssigned() =>
        AssertClean(
            """
            let items: Int[] = [1]
            for i in items {
                let y = i
            }
            """
        );

    // ---- Conditions that bind ----------------------------------------------

    [Fact]
    public void AnOptionalBindingIsAssignedInItsBranch() =>
        AssertClean(
            """
            let maybe: Int? = 1
            if let value = maybe {
                let y = value
            }
            """
        );

    [Fact]
    public void AGuardBindingOutlivesIt() =>
        AssertClean(
            """
            let maybe: Int? = 1
            guard let value = maybe else {
                return
            }
            let y = value
            """
        );

    [Fact]
    public void APatternBindingIsAssignedInItsArm() =>
        AssertClean(
            """
            match 1 {
                case let n => { let y = n }
            }
            """
        );

    // ---- Catches -----------------------------------------------------------

    [Fact]
    public void ACatchCannotRelyOnTheBody() =>
        AssertReported(
            """
            var x: Int
            do {
                x = 1
                let t = try read("a")
            } catch {
                let y = x
            }
            """,
            Throwing
        );

    [Fact]
    public void ADoAssignsWhenEveryPathDoes() =>
        AssertClean(
            """
            var x: Int
            do {
                let t = try read("a")
                x = 1
            } catch {
                x = 2
            }
            let y = x
            """,
            Throwing
        );

    const string Throwing = """
                            union IoError { Denied }
                            fn read(_ p: String) throws(IoError) -> String { return p }
                            """;

    // ---- Closures ----------------------------------------------------------

    [Fact]
    public void AClosureCannotReadWhatIsNotSetYet() =>
        AssertReported(
            """
            var x: Int
            let f = () => { return x }
            x = 1
            """
        );

    [Fact]
    public void AClosureBodyDoesNotAssignForTheCaller() =>
        AssertReported(
            """
            var x: Int
            let f = () => { x = 1 }
            let y = x
            """
        );

    [Fact]
    public void ACaptureListNameIsAssigned() =>
        AssertClean(
            """
            let outer = 1
            let f = [copied = outer] () => { return copied }
            """
        );
}
