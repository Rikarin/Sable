using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>T[k]</c> and <c>T[..k]</c> hold their elements where they are (#304,
///     docs/02 §2.5): in locals, fields, payloads, captures and globals, copied
///     whole and destroyed whole, lent to what takes a slice with nothing
///     copied — natively and in the interpreter, which must agree.
/// </summary>
[Native]
public sealed class InlineArrayTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void FixedAndBoundedArraysAreValues() =>
        Prints(
            "9\n4\n14\n3\n2\n2\nz\n8\ntrue\n1\n8\n",
            """
            struct Holder {
                var xs: Int[4]
                var name: String
            }

            fn sum(_ xs: Int[4]) -> Int {
                var total = 0
                for x in xs {
                    total += x
                }
                return total
            }

            fn make() -> Int[4] {
                return [1, 2, 3, 4]
            }
            """,
            """
                var xs: Int[4] = [4, 2, 3, 1]
                xs[0] = 8
                print(xs[0] + xs[3])
                print(xs.count)
                print(sum(xs))
                let ys = make()
                print(ys[2])
                var b: Int[..8] = []
                b.append(1)
                b.append(2)
                print(b.count)
                print(b.removeLast())
                var names: String[3] = ["a", "b", "c"]
                names[1] = "z"
                print(names[1])
                let h = Holder { xs: xs, name: "h" }
                print(h.xs[0])
                print(xs.contains(element: 3))
                xs.reverse()
                print(xs[0])
                print(h.xs[0])
            """
        );
}
