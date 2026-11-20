namespace Sable.Symbols.Tests;

/// <summary>
///     The operators of SYNTAX.md §13 as the type checker sees them:
///     <c>operator []</c> and <c>operator ()</c>, compound assignment derived
///     from a binary operator, and <c>==</c> on tuples and optionals, which
///     compares part by part with each part's own <c>==</c>.
/// </summary>
public sealed class OperatorTests {
    const string Money = """
                         struct Money {
                             var cents: Int
                             operator + (a: Money, b: Money) -> Money { return Money(cents: a.cents + b.cents) }
                             operator == (a: Money, b: Money) -> Bool { return a.cents == b.cents }
                             operator [] (m: Money, k: String) -> Int { return k.count }
                             operator () (m: Money, times: Int) -> Int { return m.cents * times }
                         }
                         struct Plain { var x: Int }
                         """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Money}\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void AnOperatorAProtocolsExtensionGivesIsFoundOnAConformer() =>
        AssertClean(
            """
            protocol Mixable {
                operator ^ (lhs: Self, rhs: Self) -> Self
            }
            extension Mixable {
                operator ^ (lhs: Self, rhs: Self) -> Self { return lhs }
            }
            struct Paint: Mixable { var hue: Int }
            fn main() {
                var p = Paint(hue: 1)
                let mixed: Paint = p ^ Paint(hue: 2)
                p ^= mixed
            }
            """
        );

    [Fact]
    public void TheWrappingOperatorsAreFixedWidthIntegers() =>
        AssertClean(
            """
            fn hash<T: FixedWidthInteger>(_ h: T, _ b: T) -> T { return (h &* b) &+ b &- h }
            fn main() {
                let a: Int = Int.max &+ 1
                var b: U8 = 250
                b &+= 10
                b &-= 1
                b &*= 3
                let c: U32 = hash(7 as U32, 9)
            }
            """
        );

    [Fact]
    public void TheWrappingOperatorsAreNotForFloats() =>
        Assert.Contains(
            ErrorsIn("fn main() { let x = 1.5 &+ 2.5 }"),
            static e => e.Code == ErrorCode.ERR_OperatorNotDefined
        );

    [Fact]
    public void AnIndexingOperatorIsCalledByIndexing() =>
        AssertClean("fn f(_ m: Money) -> Int { return m[\"x\"] }");

    [Fact]
    public void AnIndexingOperatorChecksItsIndex() {
        var error = Assert.Single(ErrorsIn("fn f(_ m: Money) -> Int { return m[3] }"));
        Assert.Equal(ErrorCode.ERR_NoMatchingOverload, error.Code);
    }

    [Fact]
    public void ACallOperatorIsCalledByCalling() =>
        AssertClean("fn f(_ m: Money) -> Int { return m(2) + m(times: 3) }");

    [Fact]
    public void WithoutOneAValueIsStillNotCallable() {
        var error = Assert.Single(ErrorsIn("fn f(_ p: Plain) -> Int { return p(2) }"));
        Assert.Equal(ErrorCode.ERR_NotCallable, error.Code);
    }

    [Fact]
    public void ACompoundAssignmentDerivesFromTheBinaryOperator() =>
        AssertClean(
            """
            fn f() -> Int {
                var m = Money(cents: 1)
                m += Money(cents: 2)
                return m.cents
            }
            """
        );

    [Fact]
    public void ACompoundAssignmentWithoutTheBinaryOperatorNamesIt() {
        var error = Assert.Single(ErrorsIn("fn f() { var m = Money(cents: 1); m -= Money(cents: 2) }"));
        Assert.Equal(ErrorCode.ERR_OperatorNotDefined, error.Code);
        Assert.Contains("'-'", error.Message);
    }

    [Fact]
    public void TuplesCompareWhenTheirElementsDo() =>
        AssertClean(
            """
            fn f(_ a: (Int, Money), _ b: (Int, Money)) -> Bool { return a == b && (1, "x") != (2, "y") }
            fn g(_ a: Money?, _ b: Money) -> Bool { return a == b || a != none }
            """
        );

    [Fact]
    public void ATupleWithAnElementThatDoesNotCompareDoesNotEither() {
        var errors = ErrorsIn("fn f(_ a: (Int, Plain), _ b: (Int, Plain)) -> Bool { return a == b }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_OperatorNotDefined && e.Message.Contains("'Plain'"));
    }

    [Fact]
    public void AStructWithoutAnEqualityOperatorDoesNotCompare() {
        var error = Assert.Single(ErrorsIn("fn f(_ a: Plain, _ b: Plain) -> Bool { return a == b }"));
        Assert.Equal(ErrorCode.ERR_OperatorNotDefined, error.Code);
    }

    [Fact]
    public void TuplesOfDifferentTypesDoNotCompare() {
        var error = Assert.Single(ErrorsIn("fn f(_ a: (Int, Int), _ b: (Int, String)) -> Bool { return a == b }"));
        Assert.Equal(ErrorCode.ERR_OperatorNotDefined, error.Code);
    }
}
