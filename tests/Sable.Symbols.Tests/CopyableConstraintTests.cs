namespace Sable.Symbols.Tests;

/// <summary>
///     The implicit <c>Copyable</c> constraint and its lifting with
///     <c>~Copyable</c> (docs/03 §3.4), and const-generic predicates, which
///     parse but have no value parameter to test yet.
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
    public void AnOrdinaryGenericFunctionIsNoCandidate() =>
        AssertReports(
            ErrorCode.ERR_NoMatchingOverload,
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
        AssertReports(ErrorCode.ERR_CannotSuppressConstraint, source);

    [Fact]
    public void AConstGenericPredicateSaysItHasNothingToTest() =>
        AssertReports(ErrorCode.ERR_ConstGenericPredicate, "struct Buffer<T, N> where N > 0 { var first: T }");
}
