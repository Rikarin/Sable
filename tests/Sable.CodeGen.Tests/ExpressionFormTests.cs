using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Expression forms of grammar.ebnf §7 that once stopped short of running:
///     tuple elements assigned, and calls with several trailing closures —
///     natively and in the interpreter, which must agree.
/// </summary>
public sealed class ExpressionFormTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void ATupleElementCanBeAssigned() =>
        Prints(
            "9\n2\n10\nhi\n",
            """
            struct Holder {
                var pair: (Int, String)
            }
            """,
            """
                var sw = (1, 2)
                sw.0 = 9
                print(sw.0)
                print(sw.1)
                var h = Holder(pair: (0, "x"))
                h.pair.0 = 10
                h.pair.1 = "hi"
                print(h.pair.0)
                print(h.pair.1)
            """
        );

    [Fact]
    public void ACallTakesSeveralTrailingClosures() =>
        Prints(
            "3\n7\n",
            """
            fn both(_ body: () -> Int, onFail: () -> Int) -> Int {
                return body() + onFail()
            }
            """,
            """
                print(both { 1 } onFail: { 2 })
                let n = both {
                    3
                } onFail: {
                    4
                }
                print(n)
            """
        );

    [Fact]
    public void AnIfGivesTheValueOfTheBranchItTakes() =>
        Prints(
            "A\nB+\nC\nann!\nnobody\nleaving\n1\n2\n8\n-1\n10\n",
            """
            fn grade(_ n: Int) -> String {
                return if n >= 90 {
                    "A"
                } else if n >= 80 {
                    let letter = "B"
                    letter + "+"
                } else {
                    "C"
                }
            }

            fn name(_ o: String?) -> String {
                let s = if let v = o { v + "!" } else { "nobody" }
                return s
            }

            fn noisy(_ flag: Bool) -> Int {
                let n = if flag {
                    defer { print("leaving") }
                    1
                } else {
                    2
                }
                return n
            }

            fn checked(_ n: Int) -> Int {
                let m = if n > 0 { n } else { return -1 }
                return m * 2
            }
            """,
            """
                print(grade(95))
                print(grade(85))
                print(grade(10))
                print(name("ann"))
                print(name(none))
                print(noisy(true))
                print(noisy(false))
                print(checked(4))
                print(checked(-4))
                print(if 1 < 2 { 10 } else { 20 })
            """
        );
}
