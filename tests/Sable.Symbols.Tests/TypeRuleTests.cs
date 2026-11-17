namespace Sable.Symbols.Tests;

/// <summary>
///     Rules of docs/03 checked on declarations: one concrete type behind
///     <c>some P</c>, no infinitely sized values, one conformance per pair.
/// </summary>
public sealed class TypeRuleTests {
    const string Shapes = """
                          protocol Shape { fn area() -> F64 }
                          struct Circle: Shape { var r: F64, fn area() -> F64 { return r } }
                          struct Square: Shape { var s: F64, fn area() -> F64 { return s } }
                          """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Shapes}\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static Diagnostic Single(ErrorCode code, string source) => Assert.Single(ErrorsIn(source), d => d.Code == code);

    // ---- some P -------------------------------------------------------------

    [Fact]
    public void SomeStandsForOneConcreteType() =>
        Single(
            ErrorCode.ERR_OpaqueReturnTypesDiffer,
            """
            fn make(_ big: Bool) -> some Shape {
                if big { return Square(s: 2) }
                return Circle(r: 1)
            }
            """
        );

    [Fact]
    public void ReturningTheSameTypeTwiceIsFine() =>
        AssertClean(
            """
            fn make(_ big: Bool) -> some Shape {
                if big { return Circle(r: 2) }
                return Circle(r: 1)
            }
            """
        );

    [Fact]
    public void AnyAllowsBoth() =>
        AssertClean(
            """
            fn make(_ big: Bool) -> any Shape {
                if big { return Square(s: 2) }
                return Circle(r: 1)
            }
            """
        );

    [Fact]
    public void EachFunctionHasItsOwnConcreteType() =>
        AssertClean(
            """
            fn one() -> some Shape { return Circle(r: 1) }
            fn two() -> some Shape { return Square(s: 1) }
            """
        );

    // ---- Infinite size ------------------------------------------------------

    [Fact]
    public void AStructCannotHoldItself() =>
        Single(ErrorCode.ERR_InfiniteSize, "struct Node { var next: Node }");

    [Fact]
    public void NorThroughAnOptional() =>
        Single(ErrorCode.ERR_InfiniteSize, "struct Node { var value: Int, var next: Node? }");

    [Fact]
    public void NorThroughAnotherStruct() {
        var errors = ErrorsIn(
            """
            struct A { var b: B }
            struct B { var a: (Int, A) }
            """
        );
        Assert.Equal(2, errors.Count(static e => e.Code == ErrorCode.ERR_InfiniteSize));
    }

    [Fact]
    public void AClassAnArrayOrAUnionBreaksTheChain() =>
        AssertClean(
            """
            class Box { var node: Node? = none }
            union Link { Next(Node), End }
            struct Node {
                var boxed: Box
                var children: Node[>..]
                var link: Link
            }
            """
        );

    // ---- Duplicate conformance ----------------------------------------------

    [Fact]
    public void ATypeConformsOnce() =>
        Single(
            ErrorCode.ERR_DuplicateConformance,
            "struct Twice: Shape, Shape { fn area() -> F64 { return 0 } }"
        );

    [Fact]
    public void AnExtensionCannotRepeatAConformance() =>
        Single(ErrorCode.ERR_DuplicateConformance, "extension Circle: Shape { }");

    // ---- Overload rejection reasons -----------------------------------------

    [Fact]
    public void ARejectedCallSaysWhichArgumentDidNotFit() =>
        Assert.Contains(
            "argument 1 is 'Int', and 'name' takes 'String'",
            Single(ErrorCode.ERR_NoMatchingOverload, "fn greet(name: String) { }\nfn probe() { greet(name: 1) }").Message,
            StringComparison.Ordinal
        );

    [Fact]
    public void ARejectedCallSaysWhichConstraintFailed() =>
        Assert.Contains(
            "'T' would be 'Int', which is not 'Shape'",
            Single(ErrorCode.ERR_NoMatchingOverload, "fn needs<T: Shape>(_ x: T) { }\nfn probe() { needs(3) }").Message,
            StringComparison.Ordinal
        );

    [Fact]
    public void EveryRejectedOverloadIsListed() {
        var message = Single(
            ErrorCode.ERR_NoMatchingOverload,
            """
            fn show(_ x: Int) { }
            fn show(_ x: String) { }
            fn probe() { show(true) }
            """
        ).Message;
        Assert.Contains("'show(_: Int)'", message, StringComparison.Ordinal);
        Assert.Contains("'show(_: String)'", message, StringComparison.Ordinal);
    }
}
