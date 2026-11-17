using Sable.Symbols;

namespace Sable.Symbols.Tests;

/// <summary>
/// Types declared inside a function body: hoisted to the function, visible
/// to the body and to nothing outside it, and checked like any other type.
/// </summary>
public sealed class LocalTypeTests
{
    private static IReadOnlyList<Diagnostic> ErrorsIn(string source) =>
        [.. Compilation.Create("module M\n" + source).GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)];

    private static void AssertClean(string source)
    {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));
    }

    private static void AssertError(ErrorCode code, string source) =>
        Assert.Contains(ErrorsIn(source), e => e.Code == code);

    [Fact]
    public void ALocalStructCanBeBuiltAndRead() => AssertClean("""
        fn probe() -> Int {
            struct Pair { var a: Int, var b: Int }
            let p = Pair(a: 1, b: 2)
            return p.a + p.b
        }
        """);

    [Fact]
    public void ALocalTypeIsUsableInAnAnnotation() => AssertClean("""
        fn probe() -> Int {
            struct Pair { var a: Int, var b: Int }
            let p: Pair = Pair { a: 1, b: 2 }
            return p.a
        }
        """);

    [Fact]
    public void ALocalTypeIsHoisted() => AssertClean("""
        fn probe() -> Int {
            let p = Pair(a: 1, b: 2)
            struct Pair { var a: Int, var b: Int }
            return p.b
        }
        """);

    [Fact]
    public void ALocalTypeMayHaveMethods() => AssertClean("""
        fn probe() -> Int {
            struct Counter {
                var n: Int
                fn doubled() -> Int { return n * 2 }
            }
            return Counter(n: 3).doubled()
        }
        """);

    [Fact]
    public void ALocalUnionCanBeMatched() => AssertClean("""
        fn probe(_ k: Int) -> String {
            union Sign { Negative, Zero, Positive }
            let s = k < 0 ? Sign.Negative : k == 0 ? Sign.Zero : Sign.Positive
            return match s {
                case .Negative => "-"
                case .Zero => "0"
                case .Positive => "+"
            }
        }
        """);

    [Fact]
    public void ALocalTypeMayConform() => AssertClean("""
        protocol Named { fn name() -> String }
        fn describe(_ value: some Named) -> String { return value.name() }
        fn probe() -> String {
            struct Anon: Named { fn name() -> String { return "anon" } }
            return describe(Anon())
        }
        """);

    [Fact]
    public void ALocalTypeThatDoesNotConformIsReported() =>
        AssertError(ErrorCode.ERR_DoesNotConform, """
            protocol Named { fn name() -> String }
            fn probe() {
                struct Anon: Named { }
            }
            """);

    [Fact]
    public void ErrorsInsideALocalTypesMethodsAreReported() =>
        AssertError(ErrorCode.ERR_TypeMismatch, """
            fn probe() {
                struct Bad { fn f() -> Int { return "no" } }
            }
            """);

    [Fact]
    public void ALocalTypeIsNotVisibleOutsideItsFunction() =>
        AssertError(ErrorCode.ERR_TypeNotFound, """
            fn first() { struct Hidden { } }
            fn second(_ h: Hidden) { }
            """);

    [Fact]
    public void ALocalTypeIsVisibleInAClosureInTheSameBody() => AssertClean("""
        fn probe() -> Int {
            struct Box { var v: Int }
            let make = (n: Int) => Box(v: n)
            return make(4).v
        }
        """);

    [Fact]
    public void ALocalTypeMayNestAnother() => AssertClean("""
        fn probe() -> Int {
            struct Outer {
                fn inner() -> Int {
                    struct Inner { var x: Int }
                    return Inner(x: 7).x
                }
            }
            return Outer().inner()
        }
        """);

    [Fact]
    public void TwoLocalTypesWithOneNameAreADuplicate() =>
        AssertError(ErrorCode.ERR_DuplicateDeclaration, """
            fn probe() {
                struct A { }
                struct A { }
            }
            """);

    [Fact]
    public void TheSameNameInTwoFunctionsIsFine() => AssertClean("""
        fn first() -> Int { struct A { var x: Int }
            return A(x: 1).x }
        fn second() -> String { struct A { var s: String }
            return A(s: "a").s }
        """);

    [Fact]
    public void ALocalAliasWorks() => AssertClean("""
        fn probe() -> Int {
            alias Count = Int
            let c: Count = 3
            return c
        }
        """);

    [Fact]
    public void AnExtensionIsNotAllowedInABody() =>
        AssertError(ErrorCode.ERR_NotAllowedInBody, """
            struct P { }
            fn probe() {
                extension P { }
            }
            """);

    [Fact]
    public void ALocalTypeMayUseTheFunctionsTypeParameters() => AssertClean("""
        fn probe<T>(_ value: T) -> T {
            struct Holder { var held: T }
            return Holder(held: value).held
        }
        """);

    [Fact]
    public void TheSemanticModelFindsTheLocalType()
    {
        var compilation = Compilation.Create("""
            module M
            fn probe() { struct Pair { var a: Int } }
            """);

        var declaration = compilation.SyntaxTrees[0].Root.DescendantNodes()
            .OfType<StructDeclarationSyntax>().Single();

        _ = compilation.GetAllDiagnostics();
        var symbol = Assert.IsType<NamedTypeSymbol>(compilation.GetDeclaredSymbol(declaration));
        Assert.Equal("Pair", symbol.Name);
        Assert.IsType<FunctionSymbol>(symbol.ContainingSymbol);
    }
}
