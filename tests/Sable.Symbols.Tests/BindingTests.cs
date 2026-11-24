namespace Sable.Symbols.Tests;

public sealed class BindingTests {
    /// <summary>Binds a function body and reports the type of its last expression.</summary>
    static string TypeOfLast(string body, string extra = "") {
        var source = TestSource.Probe(body, extra);
        var compilation = Compilation.Create(source);
        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );

        // The type of the last binding's initialiser, or of the last
        // expression statement.
        ExpressionSyntax? last = tree.Root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .LastOrDefault()
            ?.Declarator.Initializer?.Value;

        last ??= tree.Root.DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .LastOrDefault()
            ?.Expression;

        Assert.NotNull(last);

        var type = model.GetTypeOf(last!);
        Assert.NotNull(type);
        return type!.ToDisplayString();
    }

    static IReadOnlyList<Diagnostic> ErrorsIn(string body, string extra = "") {
        var source = TestSource.Probe(body, extra);
        var compilation = Compilation.Create(source);
        return [
            ..compilation.GetAllDiagnostics()
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
        ];
    }

    static void AssertClean(string body, string extra = "") {
        var errors = ErrorsIn(body, extra);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message} {e.Span}"))
        );
    }

    // ---- Literals and locals ----------------------------------------------

    [Theory]
    [InlineData("let x = 1", "Int")]
    [InlineData("let x = 1.5", "F64")]
    [InlineData("let x = true", "Bool")]
    [InlineData("let x = \"s\"", "String")]
    [InlineData("let x = 'c'", "Char")]
    [InlineData("let x: U8 = 1", "U8")]
    [InlineData("let x: F32 = 2", "F32")]
    public void BindsLiterals(string body, string expected) => Assert.Equal(expected, TypeOfLast(body));

    [Fact]
    public void ALocalKeepsItsType() {
        Assert.Equal("Int", TypeOfLast("let x = 1\nlet y = x"));
        Assert.Equal("Int", TypeOfLast("var x: Int = 1\nx"));
    }

    [Fact]
    public void AnUnknownNameIsReported() =>
        Assert.Contains(ErrorsIn("let x = nope"), static e => e.Code == ErrorCode.ERR_NameNotFound);

    [Fact]
    public void ALocalWithoutTypeOrValueIsReported() =>
        Assert.Contains(ErrorsIn("var x"), static e => e.Code == ErrorCode.ERR_CannotInferType);

    [Fact]
    public void ShadowingInTheSameScopeIsReported() =>
        Assert.Contains(
            ErrorsIn("let x = 1\nlet x = 2"),
            static e => e.Code == ErrorCode.ERR_DuplicateDeclaration
        );

    [Fact]
    public void ShadowingInANestedScopeIsAllowed() => AssertClean("let x = 1\nif true {\n    let x = 2\n}");

    // ---- Operators ---------------------------------------------------------

    [Theory]
    [InlineData("let v = 1 + 2", "Int")]
    [InlineData("let v = 1.0 * 2.0", "F64")]
    [InlineData("let v = 1 < 2", "Bool")]
    [InlineData("let v = 1 == 2", "Bool")]
    [InlineData("let v = true && false", "Bool")]
    [InlineData("let v = \"a\" + \"b\"", "String")]
    [InlineData("let v = 1 << 2", "Int")]
    [InlineData("let v = -1", "Int")]
    [InlineData("let v = !true", "Bool")]
    [InlineData("let v = ~1", "Int")]
    public void BindsOperators(string body, string expected) => Assert.Equal(expected, TypeOfLast(body));

    [Fact]
    public void ALiteralTakesTheOtherOperandsType() => Assert.Equal("U8", TypeOfLast("let a: U8 = 1\nlet v = a + 2"));

    [Theory]
    [InlineData("let v: U128 = 1 << 127", "U128")]
    [InlineData("let v: U64 = 1 + 2 * 3", "U64")]
    [InlineData("let v: F64 = 1 / 4", "F64")]
    [InlineData("let v: I8? = 3 & 5", "I8?")]
    [InlineData("let c = true\nlet v: U128 = c ? 1 << 100 : 0", "U128")]
    public void LiteralOperandsTakeTheTypeTheContextAsks(string body, string expected) =>
        Assert.Equal(expected, TypeOfLast(body));

    [Fact]
    public void AComparisonsOperandsDoNotTakeItsContextsType() =>
        Assert.Equal("Bool", TypeOfLast("let v: Bool = 1 < 2"));

    [Fact]
    public void MixingNumericTypesIsReported() =>
        Assert.Contains(
            ErrorsIn("let a: U8 = 1\nlet b: I32 = 2\nlet v = a + b"),
            static e => e.Code == ErrorCode.ERR_OperatorNotDefined
        );

    [Fact]
    public void AnOperatorOnTheWrongTypeIsReported() =>
        Assert.Contains(
            ErrorsIn("let v = true + true"),
            static e => e.Code == ErrorCode.ERR_OperatorNotDefined
        );

    [Fact]
    public void BindsAUserDefinedOperator() {
        const string extra = """
                             struct Money {
                                 var cents: I64
                                 operator + (a: Money, b: Money) -> Money {
                                     return Money { cents: a.cents + b.cents }
                                 }
                             }
                             """;

        Assert.Equal("Money", TypeOfLast("let a: Money\nlet b: Money\nlet v = a + b", extra));
    }

    // ---- Optionals ---------------------------------------------------------

    [Fact]
    public void WrapsIntoAnOptional() => Assert.Equal("Int?", TypeOfLast("let x: Int? = 1"));

    [Fact]
    public void CoalesceUnwraps() => Assert.Equal("Int", TypeOfLast("let a: Int? = 1\nlet v = a ?? 0"));

    [Fact]
    public void CoalesceOnANonOptionalIsReported() =>
        Assert.Contains(
            ErrorsIn("let a = 1\nlet v = a ?? 0"),
            static e => e.Code == ErrorCode.ERR_NotOptional
        );

    [Fact]
    public void ForceUnwrapRemovesTheOptional() => Assert.Equal("Int", TypeOfLast("let a: Int? = 1\nlet v = a!"));

    [Fact]
    public void OptionalBindingUnwraps() => AssertClean("let a: Int? = 1\nif let b = a {\n    let c: Int = b\n}");

    // ---- Members and calls -------------------------------------------------

    [Fact]
    public void BindsFieldAccess() {
        const string extra = "struct Point { var x: Int\n var y: Int }";
        Assert.Equal("Int", TypeOfLast("let p: Point\nlet v = p.x", extra));
    }

    [Fact]
    public void AnUnknownMemberIsReported() {
        const string extra = "struct Point { var x: Int }";
        Assert.Contains(
            ErrorsIn("let p: Point\nlet v = p.nope", extra),
            static e => e.Code == ErrorCode.ERR_NameNotFound
        );
    }

    [Fact]
    public void BindsACall() {
        const string extra = "fn twice(_ n: Int) -> Int { return n }";
        Assert.Equal("Int", TypeOfLast("let v = twice(21)", extra));
    }

    [Fact]
    public void BindsAMethodCall() {
        const string extra = """
                             struct Counter {
                                 var n: Int
                                 fn next() -> Int { return n }
                             }
                             """;
        Assert.Equal("Int", TypeOfLast("let c: Counter\nlet v = c.next()", extra));
    }

    [Fact]
    public void ChoosesTheRightOverload() {
        const string extra = """
                             fn f(_ a: Int) -> Int { return a }
                             fn f(_ a: String) -> Bool { return true }
                             """;

        Assert.Equal("Int", TypeOfLast("let v = f(1)", extra));
        Assert.Equal("Bool", TypeOfLast("""let v = f("s")""", extra));
    }

    [Fact]
    public void NoMatchingOverloadIsReported() {
        const string extra = "fn f(_ a: Int) -> Int { return a }";
        Assert.Contains(
            ErrorsIn("let v = f(true)", extra),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
    }

    [Fact]
    public void WrongArgumentCountIsReported() {
        const string extra = "fn f(_ a: Int) -> Int { return a }";
        Assert.Contains(
            ErrorsIn("let v = f(1, 2)", extra),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
    }

    [Fact]
    public void ArgumentLabelsAreChecked() {
        const string extra = "fn relocate(from source: Int, to dest: Int) { }";
        AssertClean("relocate(from: 1, to: 2)", extra);

        Assert.Contains(
            ErrorsIn("relocate(wrong: 1, to: 2)", extra),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
    }

    /// <summary>The spec's 'fn build(in r: region)' is called 'build(in: a)'.</summary>
    [Fact]
    public void InIsAnArgumentLabel() {
        const string extra = "fn build(in r: region, _ n: Int) -> Int { return n }";
        AssertClean("region a {\n    let n = build(in: a, 1)\n}", extra);

        Assert.Contains(
            ErrorsIn("region a {\n    let n = build(r: a, 1)\n}", extra),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
    }

    [Fact]
    public void DefaultedParametersMayBeOmitted() {
        const string extra = "fn connect(host: String, port: Int = 443) { }";
        AssertClean("""connect(host: "a")""", extra);
    }

    /// <summary>An empty array argument takes its type from the parameter.</summary>
    [Fact]
    public void AnEmptyArrayArgumentTakesTheParametersType() =>
        AssertClean("let n = count([])", "fn count(_ items: String[>..]) -> Int { return items.count }");

    /// <summary>A label may pass over parameters with defaults, never over one without.</summary>
    [Fact]
    public void ALabelMayPassOverDefaultedParametersOnly() {
        const string extra = "fn connect(host: String, port: Int = 443, secure: Bool, timeout: Int = 30) { }";
        AssertClean("""connect(host: "a", secure: true)""", extra);
        AssertClean("""connect(host: "a", secure: true, timeout: 5)""", extra);
        AssertClean("""connect(host: "a", port: 80, secure: true, timeout: 5)""", extra);
        Assert.Contains(
            ErrorsIn("""connect(host: "a", timeout: 5)""", extra),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload
        );
    }

    /// <summary>A variadic parameter's label goes on its first element, and only there (SYNTAX.md §5).</summary>
    [Fact]
    public void ALabelledVariadicTakesItsLabelOnce() {
        const string extra = "fn sum(of values: Int...) -> Int { return 0 }\nfn join(_ sep: String, items: Int...) { }";
        AssertClean("let s = sum(of: 1, 2, 3)", extra);
        AssertClean("let s = sum(of: 1)", extra);
        AssertClean("let s = sum()", extra);
        AssertClean("""join(",", items: 1, 2)""", extra);
        Assert.Contains(
            ErrorsIn("let s = sum(of: 1, of: 2)", extra),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload && e.Message.Contains("another element of 'values'")
        );
        Assert.Contains(
            ErrorsIn("let s = sum(1, 2)", extra),
            static e => e.Code == ErrorCode.ERR_NoMatchingOverload && e.Message.Contains("needs the label 'of:'")
        );
    }

    [Fact]
    public void CallingSomethingUncallableIsReported() =>
        Assert.Contains(
            ErrorsIn("let a = 1\nlet v = a()"),
            static e => e.Code == ErrorCode.ERR_NotCallable
        );

    // ---- Aggregates --------------------------------------------------------

    [Fact]
    public void BindsTuples() => Assert.Equal("(Int, String)", TypeOfLast("""let v = (1, "s")"""));

    [Fact]
    public void BindsNamedTuples() => Assert.Equal("(q: Int, r: Int)", TypeOfLast("let v = (q: 1, r: 2)"));

    [Fact]
    public void BindsArrays() => Assert.Equal("Int[]", TypeOfLast("let v = [1, 2, 3]"));

    [Fact]
    public void InconsistentArrayElementsAreReported() =>
        Assert.Contains(
            ErrorsIn("""let v = [1, "s"]"""),
            static e => e.Code == ErrorCode.ERR_InconsistentArrayElements
        );

    [Fact]
    public void IndexingAnArrayYieldsItsElement() => Assert.Equal("Int", TypeOfLast("let xs = [1, 2]\nlet v = xs[0]"));

    [Fact]
    public void IndexingADictionaryYieldsAnOptional() =>
        Assert.Equal("Int?", TypeOfLast("let d: [String: Int]\nlet v = d[\"a\"]"));

    [Fact]
    public void IndexingSomethingElseIsReported() =>
        Assert.Contains(
            ErrorsIn("let a = 1\nlet v = a[0]"),
            static e => e.Code == ErrorCode.ERR_NotIndexable
        );

    // ---- Statements --------------------------------------------------------

    [Fact]
    public void AConditionMustBeBoolean() =>
        Assert.Contains(ErrorsIn("if 1 { }"), static e => e.Code == ErrorCode.ERR_TypeMismatch);

    [Fact]
    public void BindsLoops() {
        AssertClean("while true { }");
        AssertClean("repeat { } while true");
        AssertClean("let xs = [1, 2]\nfor x in xs {\n    let y: Int = x\n}");
    }

    [Fact]
    public void BreakOutsideALoopIsReported() =>
        Assert.Contains(ErrorsIn("break"), static e => e.Code == ErrorCode.ERR_NoEnclosingLoop);

    [Fact]
    public void IteratingSomethingUniterableIsReported() =>
        Assert.Contains(ErrorsIn("for x in true { }"), static e => e.Code == ErrorCode.ERR_NotIterable);

    [Fact]
    public void AssignmentRequiresAMutableTarget() {
        AssertClean("var x = 1\nx = 2");
        Assert.Contains(
            ErrorsIn("let x = 1\nx = 2"),
            static e => e.Code == ErrorCode.ERR_NotAssignable
        );
    }

    const string Holders = """
                           class Box {
                               let size: Int
                               init(size: Int) { self.size = size }
                               fn grow() { self.size = size + 1 }
                           }

                           actor Keeper {
                               private let limit: Int
                               init(limit: Int) { self.limit = limit }
                           }
                           """;

    [Fact]
    public void AnInitialiserGivesALetFieldItsValue() {
        // The method's assignment is refused; the initialisers' are not.
        var refused = Assert.Single(ErrorsIn("", Holders));
        Assert.Equal(ErrorCode.ERR_NotAssignable, refused.Code);
        Assert.Equal(TestSource.Probe("", Holders).IndexOf("self.size = size + 1", StringComparison.Ordinal), refused.Span.Start);
    }

    [Fact]
    public void AnArrayOfLeadingDotsTakesItsTypeFromThePayload() =>
        AssertClean(
            "let g = Shape.Group(parts: [.Dot(size: 1), .Group(parts: [])])\nlet h: Shape = .Group(parts: [.Dot(size: 2)])",
            "union Shape {\n    Dot(size: Int)\n    Group(parts: Shape[>..])\n}"
        );

    [Fact]
    public void AssignmentChecksTheType() =>
        Assert.Contains(
            ErrorsIn("var x = 1\nx = \"s\""),
            static e => e.Code == ErrorCode.ERR_TypeMismatch
        );

    [Fact]
    public void ReturnIsCheckedAgainstTheSignature() {
        var compilation = Compilation.Create(
            """
            module M
            fn a() -> Int { return "s" }
            fn b() -> Int { return }
            fn c() { return 1 }
            fn d() -> Int { return 1 }
            """
        );

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_TypeMismatch);
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_ReturnValueRequired);
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_ReturnValueNotAllowed);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void BindsAnExpressionBody() {
        var compilation = Compilation.Create("module M\nfn double(_ n: Int) -> Int => n * 2");
        Assert.DoesNotContain(
            compilation.GetAllDiagnostics(),
            static d => d.Severity == DiagnosticSeverity.Error
        );
    }

    [Fact]
    public void AnExpressionBodyIsCheckedAgainstTheReturnType() =>
        Assert.Contains(
            Compilation.Create("module M\nfn f() -> Int => \"s\"").GetAllDiagnostics(),
            static e => e.Code == ErrorCode.ERR_TypeMismatch
        );

    // ---- self and members --------------------------------------------------

    [Fact]
    public void BindsSelfAndImplicitMembers() {
        var compilation = Compilation.Create(
            """
            module M
            struct Counter {
                var n: Int
                mutating fn bump() -> Int {
                    n = n + 1
                    return self.n
                }
            }
            """
        );

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => e.Message)));
    }

    [Fact]
    public void SelfOutsideATypeIsReported() =>
        Assert.Contains(ErrorsIn("let v = self"), static e => e.Code == ErrorCode.ERR_SelfOutsideType);

    // ---- The semantic model ------------------------------------------------

    [Fact]
    public void ResolvesSymbolsForNames() {
        var compilation = Compilation.Create(
            """
            module M
            fn twice(_ n: Int) -> Int { return n }
            fn probe() {
                let a = 1
                let b = twice(a)
            }
            """
        );

        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var call = tree.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single();

        var function = Assert.IsType<FunctionSymbol>(model.GetSymbolOf(call));
        Assert.Equal("twice", function.Name);

        var argument = call.ArgumentList!.Arguments[0].Expression;
        var local = Assert.IsType<LocalSymbol>(model.GetSymbolOf(argument));
        Assert.Equal("a", local.Name);
    }

    [Fact]
    public void InterpolationHolesAreBound() {
        var compilation = Compilation.Create(
            """
            module M
            fn probe() {
                let n = 1
                let s = "value \(n + 1)"
            }
            """
        );

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => e.Message)));

        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var hole = tree.Root.DescendantNodes()
            .OfType<InterpolationSyntax>()
            .Single();

        Assert.Equal("Int", model.GetTypeOf(hole.Expression)!.ToDisplayString());
    }

    // ---- Recovery ----------------------------------------------------------

    static IReadOnlyList<Diagnostic> AllErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}").GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    /// <summary>An argument that is already an error matches every overload silently.</summary>
    [Fact]
    public void AnErrorArgumentDoesNotMakeTheCallAmbiguous() {
        var errors = AllErrorsIn("fn main() { print(undefinedFn() + 1) }");
        Assert.Equal(ErrorCode.ERR_NameNotFound, Assert.Single(errors).Code);
    }

    /// <summary>A parse error is reported by the parser, not again by the binder over what is missing.</summary>
    [Theory]
    [InlineData("fn main() {\n    let x?\n}", ErrorCode.ERR_ExpectedStatementEnd)]
    [InlineData("fn main() -> Int {\n    return 1 + \n}", ErrorCode.ERR_ExpectedExpression)]
    [InlineData("struct P { var x: Int }\nfn main(_ p: P) {\n    let a = p.\n    let b = 2\n}", ErrorCode.ERR_ExpectedIdentifier)]
    public void AMissingNodeIsNotLookedUp(string source, ErrorCode code) {
        var errors = AllErrorsIn(source);
        Assert.DoesNotContain(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
        Assert.Contains(errors, e => e.Code == code);
    }

    /// <summary>'none' alone says nothing about what it is optional of.</summary>
    [Fact]
    public void NoneAloneGivesNoType() {
        var local = AllErrorsIn("fn main() { let r = none }");
        Assert.Equal(ErrorCode.ERR_CannotInferType, Assert.Single(local).Code);

        var call = AllErrorsIn("fn id<T>(_ x: T) -> T { return x }\nfn main() {\n    let q = id(none)\n    print(q)\n}");
        var error = Assert.Single(call);
        Assert.Equal(ErrorCode.ERR_CannotInferTypeArgument, error.Code);
        Assert.Contains("'none'", error.Message);
    }

    [Fact]
    public void NoneTakesItsTypeFromTheContext() {
        Assert.Empty(AllErrorsIn("fn id<T>(_ x: T) -> T { return x }\nfn main() {\n    let w: Int? = id(none)\n    let r: String? = none\n}"));
    }
}
