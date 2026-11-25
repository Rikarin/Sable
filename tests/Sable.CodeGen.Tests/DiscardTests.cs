using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary><c>_ = f()</c> makes the value and drops it where it is. Native and interpreted, leak-checked.</summary>
public sealed class DiscardTests {
    [Fact]
    public void AValueIsDroppedWhereItIsMade() =>
        Assert.Equal(
            "gone 1\nafter\ngone 5\ngone 7\ngone 8\ngone 9\n1\nend 3\ngone 3\n",
            AssertAgree(Program(
                """
                class C {
                    let n: Int
                    init(_ n: Int) { self.n = n }
                    deinit { print("gone \(n)") }
                }

                enum Failure: Error {
                    Bad,
                }

                fn make(_ n: Int) throws -> C {
                    if n > 100 {
                        throw Failure.Bad
                    }
                    return C(n)
                }

                fn generic<T>(_ x: T) -> Int {
                    _ = x
                    _ = (x, 2)
                    return 1
                }

                fn arrow(_ n: Int) => _ = generic(n)
                """,
                """
                _ = try! make(1)
                print("after")
                let c = C(3)
                _ = c
                _ = try? make(200)
                match c.n {
                    case 3 => _ = C(5)
                    case _ => _ = C(6)
                }
                let f = (n: Int) => _ = C(n)
                f(7)
                [8, 9].forEach { _ = C($0) }
                arrow(3)
                print(generic(c))
                _ = 5
                _ = [1, 2]
                print("end \(c.n)")
                """)).Stdout);
}
