using Sable.Symbols;

namespace Sable.Symbols.Tests;

public sealed class BindingTests
{
    /// <summary>Binds a function body and reports the type of its last expression.</summary>
    private static string TypeOfLast(string body, string extra = "")
    {
        var source = $"module M\n{extra}\nfn probe() {{\n{body}\n}}";
        var compilation = Compilation.Create(source);
        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var errors = compilation.GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));

        // The type of the last binding's initialiser, or of the last
        // expression statement.
        ExpressionSyntax? last = tree.Root.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>()
            .LastOrDefault()?.Declarator.Initializer?.Value;

        last ??= tree.Root.DescendantNodes()
            .OfType<ExpressionStatementSyntax>()
            .LastOrDefault()?.Expression;

        Assert.NotNull(last);

        var type = model.GetTypeOf(last!);
        Assert.NotNull(type);
        return type!.ToDisplayString();
    }

    private static IReadOnlyList<Diagnostic> ErrorsIn(string body, string extra = "")
    {
        var source = $"module M\n{extra}\nfn probe() {{\n{body}\n}}";
        var compilation = Compilation.Create(source);
        return [.. compilation.GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    private static void AssertClean(string body, string extra = "")
    {
        var errors = ErrorsIn(body, extra);
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message} {e.Span}")));
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
    public void BindsLiterals(string body, string expected) =>
        Assert.Equal(expected, TypeOfLast(body));

    [Fact]
    public void ALocalKeepsItsType()
    {
        Assert.Equal("Int", TypeOfLast("let x = 1\nlet y = x"));
        Assert.Equal("Int", TypeOfLast("var x: Int = 1\nx"));
    }

    [Fact]
    public void AnUnknownNameIsReported() =>
        Assert.Contains(ErrorsIn("let x = nope"), e => e.Code == ErrorCode.ERR_NameNotFound);

    [Fact]
    public void ALocalWithoutTypeOrValueIsReported() =>
        Assert.Contains(ErrorsIn("var x"), e => e.Code == ErrorCode.ERR_CannotInferType);

    [Fact]
    public void ShadowingInTheSameScopeIsReported() =>
        Assert.Contains(ErrorsIn("let x = 1\nlet x = 2"),
            e => e.Code == ErrorCode.ERR_DuplicateDeclaration);

    [Fact]
    public void ShadowingInANestedScopeIsAllowed() =>
        AssertClean("let x = 1\nif true {\n    let x = 2\n}");

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
    public void BindsOperators(string body, string expected) =>
        Assert.Equal(expected, TypeOfLast(body));

    [Fact]
    public void ALiteralTakesTheOtherOperandsType() =>
        Assert.Equal("U8", TypeOfLast("let a: U8 = 1\nlet v = a + 2"));

    [Fact]
    public void MixingNumericTypesIsReported() =>
        Assert.Contains(ErrorsIn("let a: U8 = 1\nlet b: I32 = 2\nlet v = a + b"),
            e => e.Code == ErrorCode.ERR_OperatorNotDefined);

    [Fact]
    public void AnOperatorOnTheWrongTypeIsReported() =>
        Assert.Contains(ErrorsIn("let v = true + true"),
            e => e.Code == ErrorCode.ERR_OperatorNotDefined);

    [Fact]
    public void BindsAUserDefinedOperator()
    {
        const string extra = """
            struct Money {
                operator + (a: Money, b: Money) -> Money { }
            }
            """;

        Assert.Equal("Money", TypeOfLast("let a: Money\nlet b: Money\nlet v = a + b", extra));
    }

    // ---- Optionals ---------------------------------------------------------

    [Fact]
    public void WrapsIntoAnOptional() =>
        Assert.Equal("Int?", TypeOfLast("let x: Int? = 1"));

    [Fact]
    public void CoalesceUnwraps() =>
        Assert.Equal("Int", TypeOfLast("let a: Int? = 1\nlet v = a ?? 0"));

    [Fact]
    public void CoalesceOnANonOptionalIsReported() =>
        Assert.Contains(ErrorsIn("let a = 1\nlet v = a ?? 0"),
            e => e.Code == ErrorCode.ERR_NotOptional);

    [Fact]
    public void ForceUnwrapRemovesTheOptional() =>
        Assert.Equal("Int", TypeOfLast("let a: Int? = 1\nlet v = a!"));

    [Fact]
    public void OptionalBindingUnwraps() =>
        AssertClean("let a: Int? = 1\nif let b = a {\n    let c: Int = b\n}");

    // ---- Members and calls -------------------------------------------------

    [Fact]
    public void BindsFieldAccess()
    {
        const string extra = "struct Point { var x: Int\n var y: Int }";
        Assert.Equal("Int", TypeOfLast("let p: Point\nlet v = p.x", extra));
    }

    [Fact]
    public void AnUnknownMemberIsReported()
    {
        const string extra = "struct Point { var x: Int }";
        Assert.Contains(ErrorsIn("let p: Point\nlet v = p.nope", extra),
            e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    [Fact]
    public void BindsACall()
    {
        const string extra = "fn twice(_ n: Int) -> Int { return n }";
        Assert.Equal("Int", TypeOfLast("let v = twice(21)", extra));
    }

    [Fact]
    public void BindsAMethodCall()
    {
        const string extra = """
            struct Counter {
                var n: Int
                fn next() -> Int { return n }
            }
            """;
        Assert.Equal("Int", TypeOfLast("let c: Counter\nlet v = c.next()", extra));
    }

    [Fact]
    public void ChoosesTheRightOverload()
    {
        const string extra = """
            fn f(_ a: Int) -> Int { return a }
            fn f(_ a: String) -> Bool { return true }
            """;

        Assert.Equal("Int", TypeOfLast("let v = f(1)", extra));
        Assert.Equal("Bool", TypeOfLast("let v = f(\"s\")", extra));
    }

    [Fact]
    public void NoMatchingOverloadIsReported()
    {
        const string extra = "fn f(_ a: Int) -> Int { return a }";
        Assert.Contains(ErrorsIn("let v = f(true)", extra),
            e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void WrongArgumentCountIsReported()
    {
        const string extra = "fn f(_ a: Int) -> Int { return a }";
        Assert.Contains(ErrorsIn("let v = f(1, 2)", extra),
            e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void ArgumentLabelsAreChecked()
    {
        const string extra = "fn relocate(from source: Int, to dest: Int) { }";
        AssertClean("relocate(from: 1, to: 2)", extra);

        Assert.Contains(ErrorsIn("relocate(wrong: 1, to: 2)", extra),
            e => e.Code == ErrorCode.ERR_NoMatchingOverload);
    }

    [Fact]
    public void DefaultedParametersMayBeOmitted()
    {
        const string extra = "fn connect(host: String, port: Int = 443) { }";
        AssertClean("connect(host: \"a\")", extra);
    }

    [Fact]
    public void CallingSomethingUncallableIsReported() =>
        Assert.Contains(ErrorsIn("let a = 1\nlet v = a()"),
            e => e.Code == ErrorCode.ERR_NotCallable);

    // ---- Aggregates --------------------------------------------------------

    [Fact]
    public void BindsTuples() =>
        Assert.Equal("(Int, String)", TypeOfLast("let v = (1, \"s\")"));

    [Fact]
    public void BindsNamedTuples() =>
        Assert.Equal("(q: Int, r: Int)", TypeOfLast("let v = (q: 1, r: 2)"));

    [Fact]
    public void BindsArrays() =>
        Assert.Equal("Int[]", TypeOfLast("let v = [1, 2, 3]"));

    [Fact]
    public void InconsistentArrayElementsAreReported() =>
        Assert.Contains(ErrorsIn("let v = [1, \"s\"]"),
            e => e.Code == ErrorCode.ERR_InconsistentArrayElements);

    [Fact]
    public void IndexingAnArrayYieldsItsElement() =>
        Assert.Equal("Int", TypeOfLast("let xs = [1, 2]\nlet v = xs[0]"));

    [Fact]
    public void IndexingADictionaryYieldsAnOptional() =>
        Assert.Equal("Int?", TypeOfLast("let d: [String: Int]\nlet v = d[\"a\"]"));

    [Fact]
    public void IndexingSomethingElseIsReported() =>
        Assert.Contains(ErrorsIn("let a = 1\nlet v = a[0]"),
            e => e.Code == ErrorCode.ERR_NotIndexable);

    // ---- Statements --------------------------------------------------------

    [Fact]
    public void AConditionMustBeBoolean() =>
        Assert.Contains(ErrorsIn("if 1 { }"), e => e.Code == ErrorCode.ERR_TypeMismatch);

    [Fact]
    public void BindsLoops()
    {
        AssertClean("while true { }");
        AssertClean("repeat { } while true");
        AssertClean("let xs = [1, 2]\nfor x in xs {\n    let y: Int = x\n}");
    }

    [Fact]
    public void BreakOutsideALoopIsReported() =>
        Assert.Contains(ErrorsIn("break"), e => e.Code == ErrorCode.ERR_NoEnclosingLoop);

    [Fact]
    public void IteratingSomethingUniterableIsReported() =>
        Assert.Contains(ErrorsIn("for x in true { }"), e => e.Code == ErrorCode.ERR_NotIterable);

    [Fact]
    public void AssignmentRequiresAMutableTarget()
    {
        AssertClean("var x = 1\nx = 2");
        Assert.Contains(ErrorsIn("let x = 1\nx = 2"),
            e => e.Code == ErrorCode.ERR_NotAssignable);
    }

    [Fact]
    public void AssignmentChecksTheType() =>
        Assert.Contains(ErrorsIn("var x = 1\nx = \"s\""),
            e => e.Code == ErrorCode.ERR_TypeMismatch);

    [Fact]
    public void ReturnIsCheckedAgainstTheSignature()
    {
        var compilation = Compilation.Create("""
            module M
            fn a() -> Int { return "s" }
            fn b() -> Int { return }
            fn c() { return 1 }
            fn d() -> Int { return 1 }
            """);

        var errors = compilation.GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        Assert.Contains(errors, e => e.Code == ErrorCode.ERR_TypeMismatch);
        Assert.Contains(errors, e => e.Code == ErrorCode.ERR_ReturnValueRequired);
        Assert.Contains(errors, e => e.Code == ErrorCode.ERR_ReturnValueNotAllowed);
        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void BindsAnExpressionBody()
    {
        var compilation = Compilation.Create("module M\nfn double(_ n: Int) -> Int => n * 2");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AnExpressionBodyIsCheckedAgainstTheReturnType() =>
        Assert.Contains(
            Compilation.Create("module M\nfn f() -> Int => \"s\"").GetAllDiagnostics(),
            e => e.Code == ErrorCode.ERR_TypeMismatch);

    // ---- self and members --------------------------------------------------

    [Fact]
    public void BindsSelfAndImplicitMembers()
    {
        var compilation = Compilation.Create("""
            module M
            struct Counter {
                var n: Int
                fn bump() -> Int {
                    n = n + 1
                    return self.n
                }
            }
            """);

        var errors = compilation.GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Message)));
    }

    [Fact]
    public void SelfOutsideATypeIsReported() =>
        Assert.Contains(ErrorsIn("let v = self"), e => e.Code == ErrorCode.ERR_SelfOutsideType);

    // ---- The semantic model ------------------------------------------------

    [Fact]
    public void ResolvesSymbolsForNames()
    {
        var compilation = Compilation.Create("""
            module M
            fn twice(_ n: Int) -> Int { return n }
            fn probe() {
                let a = 1
                let b = twice(a)
            }
            """);

        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var call = tree.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single();

        var function = Assert.IsType<FunctionSymbol>(model.GetSymbolOf(call));
        Assert.Equal("twice", function.Name);

        var argument = call.ArgumentList!.Arguments[0].Expression;
        var local = Assert.IsType<LocalSymbol>(model.GetSymbolOf(argument));
        Assert.Equal("a", local.Name);
    }

    [Fact]
    public void InterpolationHolesAreBound()
    {
        var compilation = Compilation.Create("""
            module M
            fn probe() {
                let n = 1
                let s = "value \(n + 1)"
            }
            """);

        var errors = compilation.GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => e.Message)));

        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);

        var hole = tree.Root.DescendantNodes()
            .OfType<InterpolationSyntax>().Single();

        Assert.Equal("Int", model.GetTypeOf(hole.Expression)!.ToDisplayString());
    }
}
