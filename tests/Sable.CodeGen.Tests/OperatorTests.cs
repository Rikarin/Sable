using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A type's own <c>operator []</c> and <c>operator ()</c>, compound
///     assignment from a binary operator, <c>===</c>, and <c>==</c> on tuples
///     and optionals — natively and in the interpreter, which must agree.
/// </summary>
public sealed class OperatorTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void TheWrappingOperatorsKeepTheLowBits() =>
        Prints(
            "true\n4\n255\n253\n91\n14\n",
            """
            fn mixed<T: FixedWidthInteger>(_ a: T, _ b: T) -> T { return a &* b &+ b }
            """,
            """
                print(Int.max &+ 1 == Int.min)
                var x: U8 = 250
                x &+= 10
                print(x)
                x &-= 5
                print(x)
                x &*= 3
                print(x)
                print(mixed(200 as U8, 3 as U8))
                print(2 &+ 3 &* 4)
            """
        );

    [Fact]
    public void OperatorsATypeDeclaresOrDerives() =>
        Prints(
            "8\n15\n12\ntrue\nfalse\ntrue\ntrue\nfalse\ntrue\ntrue\n",
            """
            struct Money {
                var cents: Int
                operator + (a: Money, b: Money) -> Money { return Money(cents: a.cents + b.cents) }
                operator == (a: Money, b: Money) -> Bool { return a.cents == b.cents }
                operator [] (m: Money, k: String) -> Int { return m.cents + k.count }
                operator () (m: Money, times: Int) -> Int { return m.cents * times }
            }
            class Box {
                var v: Int
                init(v: Int) { self.v = v }
            }
            """,
            """
                let m = Money(cents: 5)
                print(m["abc"])
                print(m(3))
                var total = Money(cents: 2)
                total += Money(cents: 10)
                print(total.cents)
                let a = Box(v: 1)
                let b = a
                print(a === b)
                print(a === Box(v: 1))
                print(a !== Box(v: 1))
                print((1, "x") == (1, "x"))
                print((1, "x") != (1, "x"))
                let held: Money? = Money(cents: 12)
                print(held == total)
                print(held != none)
            """
        );
}
