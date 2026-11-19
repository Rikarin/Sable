namespace Sable.Symbols.Tests;

/// <summary>
///     <c>@derive</c> (docs/06 §6.5): what it makes, what it refuses, and the
///     conditional conformance it gives a generic type.
/// </summary>
public sealed class DeriveTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create("module M\n" + source)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static Diagnostic Single(string source, ErrorCode code) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 1, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        Assert.Equal(code, errors[0].Code);
        return errors[0];
    }

    [Fact]
    public void AStructDerivesTheMembersAndTheConformances() {
        AssertClean(
            """
            @derive(Equatable, Hashable, Comparable, Debug)
            struct Point { var x: Int, var y: Int }
            fn same<T: Equatable>(_ a: T, _ b: T) -> Bool { return a == b }
            fn probe(_ p: Point, _ q: Point) -> Bool {
                let h: Int = p.hashValue
                let d: String = p.debugDescription()
                return same(p, q) && p != q && p < q && p >= q
            }
            """
        );

        var compilation = Compilation.Create("module M\n@derive(Hashable) struct Point { var x: Int }");
        var point = compilation.GetTypeByQualifiedName("M.Point")!;
        Assert.Contains(point.Members, static m => m is FunctionSymbol { Name: "==", FunctionKind: FunctionKind.Operator });
        Assert.Contains(point.Members, static m => m is FunctionSymbol { Name: "hash" });
        Assert.Equal(2, compilation.Derivations.Count(d => d.Type.Equals(point)));
    }

    [Fact]
    public void HashableAndComparableBringEquatable() =>
        AssertClean(
            """
            @derive(Hashable)
            struct Key { var id: Int }
            fn same<T: Equatable>(_ a: T, _ b: T) -> Bool { return a == b }
            fn probe(_ k: Key) -> Bool { return same(k, k) }
            """
        );

    [Fact]
    public void AUnionDerivesCaseWise() =>
        AssertClean(
            """
            @derive(Equatable, Hashable, Comparable, Debug)
            union Token { Number(Int), Word(count: Int), End }
            fn probe(_ a: Token, _ b: Token) -> Bool { return a == b || a < b || a.hashValue == 0 }
            """
        );

    [Fact]
    public void AnUnknownDerivationIsAnError() {
        var error = Single("@derive(Equatable, Printable) struct P { var x: Int }", ErrorCode.ERR_UnknownDerivation);
        Assert.Contains("'Printable'", error.Message);
    }

    [Fact]
    public void DeriveNeedsSomethingToDerive() =>
        Single("@derive struct P { var x: Int }", ErrorCode.ERR_UnknownDerivation);

    [Fact]
    public void AFieldThatDoesNotConformIsNamed() {
        var error = Single(
            """
            struct Opaque { var n: Int }
            @derive(Hashable)
            struct Holder { var count: Int, var thing: Opaque }
            """,
            ErrorCode.ERR_DerivedFieldDoesNotConform
        );
        Assert.Contains("field 'thing'", error.Message);
        Assert.Contains("'Hashable'", error.Message);
    }

    /// <summary>
    ///     An optional or an array is equal where what it holds is — '==' on
    ///     them is the language's own — but has no hash or order of its own.
    /// </summary>
    [Fact]
    public void OptionalAndArrayFieldsAreEquatableAndAnOptionalHashableWhereItsValueIs() {
        AssertClean("@derive(Equatable) struct Holder { var count: Int?, var tags: String[>..], var grid: Int[>..][>..]? }");

        // 'Int?' is Hashable through Optional's conditional conformance; an
        // optional of what is not, is not.
        AssertClean("@derive(Hashable) struct Holder { var count: Int? }");
        var error = Single("struct Opaque { var n: Int }\n@derive(Hashable) struct Holder { var count: Opaque? }", ErrorCode.ERR_DerivedFieldDoesNotConform);
        Assert.Contains("field 'count'", error.Message);
        Assert.Contains("'Hashable'", error.Message);

        var opaque = Single(
            """
            struct Opaque { var n: Int }
            @derive(Equatable)
            struct Holder { var things: Opaque[>..] }
            """,
            ErrorCode.ERR_DerivedFieldDoesNotConform
        );
        Assert.Contains("field 'things'", opaque.Message);
    }

    [Fact]
    public void APayloadThatDoesNotConformIsNamed() {
        var error = Single(
            """
            @derive(Comparable)
            union Shape { Dot, Flag(on: Bool) }
            """,
            ErrorCode.ERR_DerivedFieldDoesNotConform
        );
        Assert.Contains("payload 'on' of its case 'Flag'", error.Message);
        Assert.Contains("'Comparable'", error.Message);
    }

    [Fact]
    public void AClassCannotDerive() {
        var error = Single("@derive(Equatable) class Node { var n: Int = 0 }", ErrorCode.ERR_CannotDerive);
        Assert.Contains("identity", error.Message);
    }

    [Fact]
    public void DerivingWhatTheTypeWritesIsAnError() =>
        Single(
            """
            @derive(Equatable)
            struct Money {
                var cents: Int
                operator == (a: Money, b: Money) -> Bool { return a.cents == b.cents }
            }
            """,
            ErrorCode.ERR_CannotDerive
        );

    [Fact]
    public void AnImpliedEquatableUsesTheTypesOwn() =>
        AssertClean(
            """
            @derive(Hashable)
            struct Money {
                var cents: Int
                operator == (a: Money, b: Money) -> Bool { return a.cents == b.cents }
            }
            fn probe(_ m: Money) -> Bool { return m == m && m.hashValue == 0 }
            """
        );

    // ---- Generic types: conditional conformance --------------------------

    const string Pair = """
                        @derive(Equatable, Hashable)
                        struct Pair<A, B> { var first: A, var second: B }
                        struct Opaque { var n: Int }
                        fn same<T: Equatable>(_ a: T, _ b: T) -> Bool { return a == b }
                        """;

    [Fact]
    public void AGenericTypeConformsWhereItsArgumentsDo() =>
        AssertClean(
            $$"""
              {{Pair}}
              fn probe(_ p: Pair<Int, Bool>, _ q: Pair<Pair<Int, Int>, Int>) -> Bool {
                  return p == p && same(p, p) && same(q, q) && q.hashValue == 0
              }
              """
        );

    [Fact]
    public void AGenericTypeDoesNotConformWhereAnArgumentDoesNot() {
        var error = Single(
            $$"""
              {{Pair}}
              fn probe(_ p: Pair<Int, Opaque>) -> Bool { return same(p, p) }
              """,
            ErrorCode.ERR_NoMatchingOverload
        );
        Assert.Contains("Equatable", error.Message);
    }

    [Fact]
    public void ADerivedOperatorOnANonConformingConstructionSaysWhichField() {
        var error = Single(
            $$"""
              {{Pair}}
              fn probe(_ p: Pair<Int, Opaque>) -> Bool { return p == p }
              """,
            ErrorCode.ERR_DoesNotConform
        );
        Assert.Contains("field 'second'", error.Message);
    }

    [Fact]
    public void ADerivedMethodOnANonConformingConstructionSaysWhichField() {
        var error = Single(
            $$"""
              {{Pair}}
              fn probe(_ p: Pair<Opaque, Int>) {
                  var h = Hasher()
                  p.hash(into: &h)
              }
              """,
            ErrorCode.ERR_DoesNotConform
        );
        Assert.Contains("field 'first'", error.Message);
    }

    [Fact]
    public void AGenericFieldThatCanNeverConformIsAnErrorAtTheDeclaration() {
        var error = Single(
            """
            struct Opaque { var n: Int }
            @derive(Equatable)
            struct Tagged<T> { var value: T, var tag: Opaque }
            """,
            ErrorCode.ERR_DerivedFieldDoesNotConform
        );
        Assert.Contains("field 'tag'", error.Message);
    }

    [Fact]
    public void ARecursiveGenericUnionConformsCoinductively() =>
        AssertClean(
            """
            @derive(Equatable)
            union Chain<T> { End, Link(T, Chain<T>) }
            fn same<T: Equatable>(_ a: T, _ b: T) -> Bool { return a == b }
            fn probe(_ c: Chain<Int>) -> Bool { return same(c, c) }
            """
        );

    [Fact]
    public void AnEnumDerivesByCase() =>
        AssertClean(
            """
            @derive(Equatable, Hashable, Comparable, Debug)
            enum Color { Red, Green, Blue }
            fn probe() -> Bool { return Color.Red < Color.Blue && Color.Red != Color.Green && Color.Red.hashValue != 0 }
            """
        );

    [Fact]
    public void AFlagsEnumCannotDerive() {
        var error = Single("@derive(Equatable) @flags enum Mode { Read, Write }", ErrorCode.ERR_CannotDerive);
        Assert.Contains("@flags", error.Message);
    }
}
