using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Constructs the corpus used before lowering handled them: enum equality,
///     F64 remainder, untyped and union throws, rethrows, function conversions
///     that add a throw, and 'some P' parameters — natively and interpreted.
/// </summary>
public sealed class LoweringGapTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    // ---- Enum equality -------------------------------------------------------

    [Fact]
    public void EnumsWithoutPayloadsCompareByCase() =>
        Prints(
            "true\nfalse\nfalse\ntrue\ntrue\n",
            """
            enum Method { Get, Post, Put, Delete }

            fn same(_ a: Method, _ b: Method) -> Bool { return a == b }

            fn differ(_ a: Method, _ b: Method) -> Bool { return a != b }
            """,
            """
                print(same(Method.Get, .Get))
                print(same(Method.Get, .Delete))
                print(differ(Method.Put, .Put))
                print(differ(Method.Put, .Post))
                let m = Method.Delete
                print(m == Method.Delete)
            """
        );

    [Fact]
    public void AnEnumWithRawValuesComparesByCase() =>
        Prints(
            "true\nfalse\n",
            """
            enum Status: U16 {
                Ok = 200
                Missing = 404
            }
            """,
            """
                let s = Status.Ok
                print(s == Status.Ok)
                print(s == Status.Missing)
            """
        );

    [Fact]
    public void AnEnumsOwnEqualityIsTheOneUsed() =>
        // Every Shade is equal to every other: its '==' says so.
        Prints(
            "true\nfalse\n",
            """
            enum Shade {
                Light
                Dark

                operator == (a: Shade, b: Shade) -> Bool { return true }
            }
            """,
            """
                print(Shade.Light == Shade.Dark)
                print(Shade.Light != Shade.Dark)
            """
        );

    [Fact]
    public void AnEquatableEnumMeetsAGenericConstraint() =>
        Prints(
            "true\nfalse\n",
            """
            enum Suit: Equatable { Hearts, Spades }

            fn has<T: Equatable>(_ values: T[], _ wanted: T) -> Bool {
                for v in values {
                    if v == wanted { return true }
                }
                return false
            }
            """,
            """
                print(has([Suit.Hearts], Suit.Hearts))
                print(has([Suit.Hearts], Suit.Spades))
            """
        );

    // ---- F64 remainder -------------------------------------------------------

    [Fact]
    public void TheRemainderOfFloatsIsFmod() =>
        Prints(
            "1.5\n-1.5\n1.5\n0\n",
            """
            fn rem(_ a: F64, _ b: F64) -> F64 { return a % b }
            """,
            """
                print(rem(7.5, 2.0))
                print(rem(-7.5, 2.0))
                print(rem(7.5, -2.0))
                print(rem(6.0, 3.0))
            """
        );

    // ---- 'some P' parameters -------------------------------------------------

    [Fact]
    public void ASomeParameterIsAnAnonymousGeneric() =>
        // One instance per type passed; one 'some P' passed on to another.
        Prints(
            "point 1\nlabel hi\n<point 1>\n<label hi>\n",
            """
            protocol Describable {
                fn describe() -> String
            }

            struct Point: Describable {
                var x: Int
                fn describe() -> String { return "point \(x)" }
            }

            struct Label: Describable {
                var text: String
                fn describe() -> String { return "label " + text }
            }

            fn describeAny(_ value: some Describable) -> String {
                return value.describe()
            }

            fn bracketed(_ value: some Describable) -> String {
                return "<" + describeAny(value) + ">"
            }
            """,
            """
                print(describeAny(Point(x: 1)))
                print(describeAny(Label(text: "hi")))
                print(bracketed(Point(x: 1)))
                print(bracketed(Label(text: "hi")))
            """
        );
}
