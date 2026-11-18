namespace Sable.Symbols.Tests;

/// <summary>
///     The implicit <c>Copyable</c> constraint and its lifting with
///     <c>~Copyable</c> (docs/03 §3.4), and value generic parameters with
///     the const-generic predicates over them.
/// </summary>
public sealed class CopyableConstraintTests {
    const string Handle = """
                          @noncopy struct Handle {
                              var id: Int
                          }
                          protocol Named {
                              fn name() -> String
                          }
                          @noncopy struct Tag: Named {
                              fn name() -> String { return "tag" }
                          }
                          """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Handle}\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static void AssertReports(ErrorCode code, string source) {
        var errors = ErrorsIn(source);
        Assert.True(
            errors.Count > 0 && errors.All(e => e.Code == code),
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    [Fact]
    public void ALiftedParameterTakesAMoveOnlyType() =>
        AssertClean(
            """
            struct Box<T: ~Copyable> {
                var value: T
            }
            fn take<T: ~Copyable>(consuming _ x: T) { }
            fn f() {
                let b = Box<Handle> { value: Handle { id: 1 } }
                take(Handle { id: 2 })
            }
            """
        );

    [Fact]
    public void AnOrdinaryParameterDoesNot() =>
        AssertReports(
            ErrorCode.ERR_NoncopyableTypeArgument,
            """
            struct Box<T> {
                var value: T
            }
            fn f() -> Box<Handle> {
                return Box<Handle> { value: Handle { id: 1 } }
            }
            """
        );

    [Fact]
    public void AnOrdinaryGenericFunctionRefusesIt() =>
        AssertReports(
            ErrorCode.ERR_NoncopyableTypeArgument,
            """
            fn take<T>(_ x: T) { }
            fn f() {
                take(Handle { id: 2 })
            }
            """
        );

    [Fact]
    public void ALiftedParameterDoesNotPassToAnOrdinaryOne() =>
        AssertReports(
            ErrorCode.ERR_NoncopyableTypeArgument,
            """
            struct Box<T> {
                var value: T
            }
            struct Outer<T: ~Copyable> {
                var inner: Box<T>
            }
            """
        );

    [Fact]
    public void AWhereClauseLiftsItTooAlongsideAConformance() =>
        AssertClean(
            """
            fn label<T>(consuming _ x: T) -> String where T: Named & ~Copyable {
                return x.name()
            }
            fn f() -> String {
                return label(Tag())
            }
            """
        );

    [Fact]
    public void TheConformanceStillHolds() =>
        AssertReports(
            ErrorCode.ERR_NoMatchingOverload,
            """
            fn label<T: Named & ~Copyable>(consuming _ x: T) -> String {
                return x.name()
            }
            fn f() -> String {
                return label(Handle { id: 1 })
            }
            """
        );

    [Theory]
    [InlineData("struct Box<T: ~Named> { }")]
    [InlineData("struct Box<T> { var value: ~Copyable }")]
    [InlineData("fn f(_ x: ~Copyable) { }")]
    public void OnlyCopyableIsLiftedAndOnlyInAConstraint(string source) =>
        AssertReports(ErrorCode.ERR_MisplacedSuppression, source);

    [Fact]
    public void AConstGenericPredicateSaysItHasNothingToTest() =>
        AssertReports(ErrorCode.ERR_ConstGenericPredicate, "struct Buffer<T, N> where N > 0 { var first: T }");

    // ---- Value generic parameters --------------------------------------------

    const string Buffer = """
                          struct Buf<T, N: Int> where N > 0, N % 2 == 0 {
                              var items: T[N]
                              fn count() -> Int { return N }
                          }
                          """;

    [Fact]
    public void AValueParameterIsANumberInTheBodyAndSizesAnArray() {
        AssertClean(Buffer + "\nfn f() -> Int { let b = Buf<Int, 2> { items: [1, 2] }\n return b.count() }");
        var compilation = Compilation.Create($"module M\n{Buffer}");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var buf = (NamedTypeSymbol)compilation.GetModule("M")!.GetMembers("Buf").Single();
        Assert.True(buf.TypeParameters[1].IsValue);
        Assert.False(buf.TypeParameters[0].IsValue);
        var items = buf.Members.OfType<FieldSymbol>().Single(static f => f.Name == "items");
        Assert.Equal("T[N]", items.Type.ToDisplayString());

        // An instance is a type of its own, with the number in its array.
        var four = buf.Construct([compilation.Core.Int, new ValueTypeArgumentSymbol(4)]);
        Assert.Equal("Buf<Int, 4>", four.ToDisplayString());
        Assert.Equal("Int[4]", TypeSubstitution.For(four).Apply(items.Type).ToDisplayString());
        Assert.NotEqual(four, buf.Construct([compilation.Core.Int, new ValueTypeArgumentSymbol(6)]));
    }

    [Theory]
    [InlineData("let b: Buf<Int, 0>? = none")]
    [InlineData("let b: Buf<Int, 3>? = none")]
    public void AValueTheTypesPredicateRefusesIsReported(string body) =>
        AssertReports(ErrorCode.ERR_ConstGenericPredicateFails, Buffer + $"\nfn f() {{ {body} }}");

    [Theory]
    [InlineData("let b: Buf<Int, String>? = none")]
    [InlineData("let b: Buf<4, 4>? = none")]
    public void ANumberAndATypeAreNotGivenForEachOther(string body) =>
        AssertReports(ErrorCode.ERR_ValueGenericArgument, Buffer + $"\nfn f() {{ {body} }}");

    [Theory]
    [InlineData("struct Bad<N: Int> where N > limit { }")]
    [InlineData("struct Bad<N: Int> where N > count() { }")]
    [InlineData("fn bad<N: Int>() where N > 0 { }")]
    public void APredicateIsMadeOfValueParametersNumbersAndOperators(string source) =>
        AssertReports(ErrorCode.ERR_ConstGenericPredicate, source);
}
