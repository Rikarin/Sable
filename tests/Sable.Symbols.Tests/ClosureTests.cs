namespace Sable.Symbols.Tests;

/// <summary>
///     Closures: what their parameters and result are, where those come from, and
///     what they can see of the scope around them.
/// </summary>
public sealed class ClosureTests {
    const string Helpers = """
                           fn apply(_ v: Int, _ f: (Int) -> Int) -> Int { return f(v) }
                           fn project<T, U>(_ v: T, _ f: (T) -> U) -> U { return f(v) }
                           fn run(_ f: () -> Void) { f() }
                           """;

    static (Compilation, SyntaxTree) Compile(string body, string extra = "") {
        var source = $"module M\n{Helpers}\n{extra}\nfn probe() {{\n{body}\n}}";
        var compilation = Compilation.Create(source);
        return (compilation, compilation.SyntaxTrees[0]);
    }

    static IReadOnlyList<Diagnostic> ErrorsIn(string body, string extra = "") {
        var (compilation, _) = Compile(body, extra);
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

    /// <summary>The type of the last local binding's initialiser.</summary>
    static string TypeOfLast(string body, string extra = "") {
        var (compilation, tree) = Compile(body, extra);
        var model = compilation.GetSemanticModel(tree);

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );

        var last = tree.Root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .LastOrDefault()
            ?.Declarator.Initializer?.Value;

        Assert.NotNull(last);
        var type = model.GetTypeOf(last!);
        Assert.NotNull(type);
        return type!.ToDisplayString();
    }

    // ---- Shape -------------------------------------------------------------

    [Theory]
    [InlineData("let f = (x: Int) => x * 2", "(Int) -> Int")]
    [InlineData("let f = (a: Int, b: Int) => a + b", "(Int, Int) -> Int")]
    [InlineData("let f = () => { }", "() -> Void")]
    [InlineData("let f = (x: Int) => x > 0", "(Int) -> Bool")]
    [InlineData("let f = (x: Int) -> I64 => 0", "(Int) -> I64")]
    public void AClosureTakesItsShapeFromWhatIsWritten(string body, string expected) =>
        Assert.Equal(expected, TypeOfLast(body));

    [Fact]
    public void AnOmittedReturnTypeComesFromTheBody() =>
        Assert.Equal(
            "(Int) -> String",
            TypeOfLast(
                """
                let f = (x: Int) => {
                    return "n"
                }
                """
            )
        );

    [Fact]
    public void AClosureThatReturnsNothingReturnsVoid() =>
        Assert.Equal(
            "(Int) -> Void",
            TypeOfLast(
                """
                var total = 0
                let f = (x: Int) => { total = x }
                """
            )
        );

    // ---- Inference from the context ---------------------------------------

    [Fact]
    public void AParameterTypeComesFromTheCall() => AssertClean("let r = apply(1, x => x + 1)");

    [Fact]
    public void ShorthandParametersComeFromTheCall() => AssertClean("let r = apply(1, { $0 + 1 })");

    [Fact]
    public void AnAnnotationSuppliesTheParameterType() =>
        Assert.Equal("(Int) -> Int", TypeOfLast("let f: (Int) -> Int = x => x"));

    [Fact]
    public void AClosureResultSettlesATypeParameter() =>
        AssertClean(
            """
            let r: Bool = project(1, x => x > 0)
            """
        );

    [Fact]
    public void ATrailingClosureIsAnArgument() => AssertClean("let r = apply(1) { $0 * 2 }");

    [Fact]
    public void AClosureParameterWithNothingToInferFromIsAnError() {
        var errors = ErrorsIn("let f = x => x");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_CannotInferType);
    }

    [Fact]
    public void ShorthandOutsideAKnownSignatureIsAnError() {
        var errors = ErrorsIn("let f = { $0 + 1 }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    // ---- Captures ----------------------------------------------------------

    [Fact]
    public void AClosureSeesTheScopeAroundIt() =>
        AssertClean(
            """
            let offset = 10
            let f = (x: Int) => x + offset
            """
        );

    [Fact]
    public void ACaptureListIntroducesItsOwnName() =>
        AssertClean(
            """
            let outer = 1
            let f = [copied = outer] (x: Int) => x + copied
            """
        );

    [Fact]
    public void ACaptureListNameIsNotVisibleOutside() {
        var errors = ErrorsIn(
            """
            let outer = 1
            let f = [copied = outer] (x: Int) => x + copied
            let bad = copied
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    // ---- Calling a value ---------------------------------------------------

    [Fact]
    public void AValueOfFunctionTypeIsCallable() =>
        Assert.Equal(
            "Int",
            TypeOfLast(
                """
                let f = (x: Int) => x
                let r = f(1)
                """
            )
        );

    [Fact]
    public void CallingWithTheWrongCountIsAnError() {
        var errors = ErrorsIn(
            """
            let f = (x: Int) => x
            let r = f(1, 2)
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void AClosureBodyIsCheckedAgainstItsParameterTypes() {
        var errors = ErrorsIn("let r = apply(1, x => x && true)");
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void AOneExpressionBraceClosureReturnsIt() =>
        Assert.Equal("(Int) -> Int", TypeOfLast("let f: (Int) -> Int = { $0 * 2 }"));

    [Fact]
    public void AOneExpressionBraceClosureIsCheckedAgainstTheResult() {
        var errors = ErrorsIn("let r = apply(1) { $0 > 0 }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_TypeMismatch);
    }

    [Fact]
    public void ABraceClosureOfSeveralStatementsStillNeedsAReturn() {
        var errors = ErrorsIn(
            """
            let r = apply(1) {
                let doubled = $0 * 2
            }
            """
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotAllPathsReturn);
    }
}
