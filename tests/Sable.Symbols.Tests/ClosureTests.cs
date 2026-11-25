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

    [Theory]
    [InlineData("let f = (v: inout Int, by: Int) => { v += by }", "(inout Int, Int) -> Void")]
    [InlineData("let f: (inout Int) -> () = { $0 += 1 }", "(inout Int) -> Void")]
    [InlineData("let f: (inout Int) -> () = { v in v = 2 }", "(inout Int) -> Void")]
    public void AClosuresParameterMayBeInout(string body, string expected) =>
        Assert.Equal(expected, TypeOfLast(body));

    [Fact]
    public void AnInoutClosureIsCalledWithAPlaceItMayChange() {
        AssertClean("var n = 1\nlet f: (inout Int) -> () = { $0 += 1 }\nf(&n)");
        Assert.Contains(ErrorsIn("let n = 1\nlet f: (inout Int) -> () = { $0 += 1 }\nf(&n)"), static e => e.Code == ErrorCode.ERR_NotAssignable);
        Assert.Contains(ErrorsIn("let f: (inout Int) -> () = { (v: Int) in print(v) }"), static e => e.Code == ErrorCode.ERR_TypeMismatch);
    }

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
    public void AClosureIsNoValueOfAConcreteTypeSoAnOverloadTakingOneDoesNotFit() {
        // Each pair takes a value or a closure in the same place; a closure
        // argument fits only the second.
        const string extra = """
                             enum Mode { Fast, Slow }
                             fn replace(_ n: Int, with text: String) -> String { return text }
                             fn replace(_ n: Int, with make: (Int) -> String) -> String { return make(n) }
                             fn pick(_ mode: Mode) -> Int { return 1 }
                             fn pick(_ make: () -> Mode) -> Int { return 2 }
                             """;
        Assert.Equal("String", TypeOfLast("let r = replace(1, with: { n in \"\\(n)\" })", extra));
        Assert.Equal("String", TypeOfLast("let r = replace(1, with: \"x\")", extra));
        Assert.Equal("Int", TypeOfLast("let r = pick({ .Slow })", extra));
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

    // ---- Weak and unowned captures ----------------------------------------

    const string Counter = """
                           class Counter {
                               var total: Int = 10
                               init() { }
                               fn bump() -> Int { total += 1
                                   return total }
                               fn adder() -> (Int) -> Int {
                                   let localCount = 2
                                   return [weak self, count = localCount] (x: Int) => {
                                       guard let self else { return 0 }
                                       return self.total + count + x + bump()
                                   }
                               }
                               fn other() -> () -> Int {
                                   return [weak self] () => {
                                       if let s = self { return s.total }
                                       return -1
                                   }
                               }
                               fn peek() -> () -> Int { return [unowned self] () => total + self.bump() }
                           }
                           """;

    [Fact]
    public void AWeakSelfIsOptionalInTheClosure() => AssertClean("", Counter);

    [Fact]
    public void AWeakNameIsOptionalInTheClosure() =>
        AssertClean(
            """
            let c = Counter()
            let f = [weak c] () => {
                guard let c else { return 0 }
                return c.total
            }
            let g = [weak d = c] () => d?.total ?? 0
            """,
            Counter);

    [Fact]
    public void AWeakCaptureIsAFreshOptionalLocal() {
        var (compilation, tree) = Compile("let c = Counter()\nlet f = [weak c] () => c", Counter);
        var model = compilation.GetSemanticModel(tree);
        var closure = tree.Root.DescendantNodes().OfType<ClosureExpressionSyntax>().Last();
        var bound = Assert.IsType<BoundClosure>(model.GetBoundNode(closure));
        var capture = Assert.Single(bound.Captures);
        Assert.Equal(CaptureKind.Weak, capture.CaptureKind);
        Assert.Equal("Counter?", Assert.IsType<LocalSymbol>(capture.Symbol).Type.ToDisplayString());
        Assert.Equal("() -> Counter?", bound.Type.ToDisplayString());
    }

    [Fact]
    public void AMemberOfAWeakSelfNeedsItUnwrapped() {
        var errors = ErrorsIn(
            "",
            """
            class Counter {
                var total: Int = 10
                init() { }
                fn f() -> () -> Int { return [weak self] () => total }
            }
            """);
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound && e.Message.Contains("guard let self", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("let n = 1\nlet f = [weak n] () => 0", ErrorCode.ERR_WeakNeedsOptionalClass)]
    [InlineData("let c: Counter? = none\nlet f = [unowned c] () => 0", ErrorCode.ERR_UnownedNeedsClass)]
    public void OnlyAClassInstanceIsCapturedWeakOrUnowned(string body, ErrorCode code) =>
        Assert.Contains(ErrorsIn(body, Counter), e => e.Code == code);

    [Fact]
    public void TheEmptyTupleIsVoid() {
        // #261: '()' is the unit type by its other name, so a closure given
        // for '(Int) -> ()' needs no value, as one for '(Int) -> Void' does not.
        AssertClean(
            """
            var sum = 0
            each { sum += $0 }
            each { x in
                sum += x
            }
            let f: () -> Void = nothing
            let g: () -> () = f
            let unit: () = ()
            g()
            """,
            """
            fn each(_ body: (Int) -> ()) { body(1) }
            fn nothing() -> () { }
            """);
        Assert.Equal("Void", TypeOfLast("let u = ()"));
    }

    // ---- Trailing closures for labelled parameters ------------------------------

    const string Labelled = """
                            fn replacing(_ r: Int, with f: (Int) -> Int) -> Int { return f(r) }
                            fn both(_ r: Int, then a: (Int) -> Int, finally b: (Int) -> Int) -> Int { return b(a(r)) }
                            fn scaled(_ r: Int, scale: Int = 2, by f: (Int) -> Int) -> Int { return f(r) * scale }
                            fn maybe(_ r: Int, with f: ((Int) -> Int)? = none) -> Int { return f?(r) ?? r }
                            fn later<T>(_ make: () -> T) -> T { return make() }
                            fn spanned<R>(_ name: String, tags: @autoclosure () -> Int[>..] = [], _ body: (Int) -> R) -> R { return body(tags().count) }
                            """;

    [Theory]
    [InlineData("let v = replacing(3) { $0 + 1 }", "Int")]
    [InlineData("let v = both(3) { $0 + 1 } finally: { $0 * 10 }", "Int")]
    [InlineData("let v = scaled(3) { $0 + 1 }", "Int")]
    [InlineData("let v = scaled(3, scale: 4) { $0 + 1 }", "Int")]
    [InlineData("let v = maybe(3) { $0 + 1 }", "Int")]
    [InlineData("let v = later { 1.5 }", "F64")]
    [InlineData("let v = spanned(\"a\") { $0 > 0 }", "Bool")]
    [InlineData("let v = spanned(\"a\", tags: [1]) { $0 }", "Int")]
    public void ATrailingClosureGoesToTheNextParameterThatTakesOneLabelledOrNot(string body, string expected) =>
        Assert.Equal(expected, TypeOfLast(body, Labelled));

    [Fact]
    public void AnArgumentInParenthesesStillNeedsItsLabel() =>
        Assert.Contains(ErrorsIn("let v = replacing(3, { $0 + 1 })", Labelled), static e => e.Code == ErrorCode.ERR_NoMatchingOverload);

    [Fact]
    public void ATrailingClosureWithNoParameterLeftIsReported() =>
        Assert.NotEmpty(ErrorsIn("let v = replacing(3, with: { $0 }) { $0 + 1 }", Labelled));

    // ---- An implicit member or a closure fits a parameter of its shape --------------

    const string Modes = """
                         enum Mode { Fast, Slow }
                         fn pick(_ m: Mode) -> String { return "value" }
                         fn pick(_ m: () -> Mode) -> String { return "closure" }
                         """;

    [Fact]
    public void AnImplicitMemberIsNotPassedWhereAFunctionGoes() {
        var (compilation, tree) = Compile("let v = pick(.Fast)\nlet w = pick { .Slow }\nlet x = pick({ .Fast })", Modes);
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var model = compilation.GetSemanticModel(tree);
        var chosen = tree.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(static c => c.Expression is IdentifierNameSyntax { Identifier.Text: "pick" })
            .Select(c => ((FunctionSymbol)model.GetSymbolOf(c.Expression)!).Parameters[0].Type.ToDisplayString())
            .ToList();
        Assert.Equal(["Mode", "() -> Mode", "() -> Mode"], chosen);
    }
}
