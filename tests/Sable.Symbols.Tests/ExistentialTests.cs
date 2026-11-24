namespace Sable.Symbols.Tests;

/// <summary>
///     <c>any P</c> in the type checker (docs/03 §3.2): what converts to one,
///     and which requirements can be reached through one.
/// </summary>
public sealed class ExistentialTests {
    const string Protocols = """
                             protocol Shape {
                                 fn area() -> Int
                             }
                             protocol Same {
                                 fn same(_ other: Self) -> Bool
                                 fn label() -> String
                             }
                             protocol Bag {
                                 associatedtype Item
                                 fn first() -> Item?
                                 fn size() -> Int
                             }
                             struct Square: Shape, Same {
                                 var side: Int
                                 fn area() -> Int { return side * side }
                                 fn same(_ other: Square) -> Bool { return side == other.side }
                                 fn label() -> String { return "square" }
                             }
                             struct Words: Bag {
                                 alias Item = String
                                 fn first() -> String? { return none }
                                 fn size() -> Int { return 0 }
                             }
                             """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Protocols}\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void AValueConvertsToAnOptionalExistentialAndThroughAs() =>
        AssertClean(
            """
            fn f() -> Int {
                let shape = Square(side: 2) as any Shape
                let maybe: (any Shape)? = Square(side: 3)
                return shape.area() + (maybe?.area() ?? 0)
            }
            """
        );

    [Fact]
    public void RequirementsThatDoNotMentionSelfAreReachable() =>
        AssertClean(
            """
            fn f(_ s: any Same, _ b: any Bag) -> String {
                return s.label() + "\(b.size())"
            }
            """
        );

    [Theory]
    [InlineData("fn f(_ a: any Same, _ b: any Same) -> Bool { return a.same(b) }")]
    [InlineData("fn f(_ b: any Bag) -> Bool { return b.first() == none }")]
    public void ARequirementAboutSelfOrAnAssociatedTypeIsNot(string source) =>
        Assert.Contains(ErrorsIn(source), static e => e.Code == ErrorCode.ERR_SelfRequirementOnExistential);

    [Fact]
    public void ThroughAGenericParameterItIs() =>
        AssertClean("fn f<T: Same>(_ a: T, _ b: T) -> Bool { return a.same(b) }");

    const string Opening = """
                           protocol Named {
                               fn name() -> String
                           }
                           protocol Grows {
                               associatedtype Unit: Named
                               associatedtype Pair: Named & Shape
                               associatedtype Raw
                               fn doubled() -> Self
                               fn unit() -> Unit
                               fn pair() -> Pair
                               fn raw() -> Raw
                               var me: Self { get }
                               var settable: Self { get set }
                               fn more() async -> Self
                           }
                           """;

    /// <summary>
    ///     What a call through <c>any P</c> gives back once the existential is
    ///     opened (#299, SE-0352): Self as the existential itself, an
    ///     associated type as the existential of the protocols it is
    ///     constrained to, a composition for several.
    /// </summary>
    [Theory]
    [InlineData("g.doubled()", "any Grows")]
    [InlineData("g.me", "any Grows")]
    [InlineData("g.doubled().doubled()", "any Grows")]
    [InlineData("g.unit()", "any Named")]
    [InlineData("g.pair()", "any Named & Shape")]
    [InlineData("both.doubled()", "any Grows & Shape")]
    public void AResultOfSelfOrAnAssociatedTypeIsErasedThroughAnExistential(string expression, string type) {
        AssertClean($"{Opening}\nfn f(_ g: any Grows, _ both: any Grows & Shape) -> {type} {{\n    return {expression}\n}}\n");

        // As that existential, and not as something narrower.
        Assert.Contains(
            ErrorsIn($"{Opening}\nfn f(_ g: any Grows, _ both: any Grows & Shape) -> Square {{\n    return {expression}\n}}\n"),
            static e => e.Code == ErrorCode.ERR_TypeMismatch
        );
    }

    [Theory]
    [InlineData("g.raw()", ErrorCode.ERR_ExistentialResultNotErasable)]
    [InlineData("g.settable", ErrorCode.ERR_SelfRequirementOnExistential)]
    [InlineData("g.more()", ErrorCode.ERR_SelfRequirementOnExistential)]
    public void WhatCannotBeErasedStaysOutOfReach(string expression, ErrorCode code) =>
        Assert.Contains(ErrorsIn($"{Opening}\nfn f(_ g: any Grows) async {{\n    let x = await {expression}\n}}\n"), e => e.Code == code);
}
