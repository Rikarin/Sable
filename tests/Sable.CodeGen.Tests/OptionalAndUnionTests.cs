using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Optionals and structural unions, natively and in the interpreter:
///     promotion that takes a step first, recursive unions, narrowing after
///     an early return, and chains through optional members.
/// </summary>
public sealed class OptionalAndUnionTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void PromotionThroughALiteralOrAUnion() =>
        Prints(
            "2\nx\n7\n",
            "",
            """
                let xs: Int[>..]? = [9, 8]
                print(xs?.count ?? 0)
                let u: (Int | String)? = "x"
                if let v = u {
                    if v is String { print(v) }
                }
                print((none as Int?) ?? 7)
            """
        );

    [Fact]
    public void ARecursiveUnionAndNarrowingAfterAReturn() =>
        Prints(
            "8\n[3]\n{2}\nnum\n",
            """
            alias Json = Bool | F64 | String | Json[] | [String: Json]

            fn leaves(_ j: Json) -> Int {
                if j is Json[] {
                    var n = 0
                    for x in j { n = n + leaves(x) }
                    return n
                }
                if j is [String: Json] {
                    var n = 0
                    for (k, v) in j { n = n + leaves(v) }
                    return n
                }
                return 1
            }

            fn render(_ j: Json) -> String {
                if j is Bool { return "bool" }
                if j is F64 { return "num" }
                if j is String { return j }
                if j is Json[] { return "[\(j.count)]" }
                return "{\(j.count)}"
            }
            """,
            """
                let inner: Json = [1.5, "x", false]
                let object: Json = ["a": inner, "b": "y"]
                let all: Json = [object, inner, true]
                print(leaves(all))
                print(render(all))
                print(render(object))
                print(render(1.5))
            """
        );

    [Fact]
    public void ChainsThroughOptionalMembersCallsAndStores() =>
        Prints(
            "Prague\nunknown\n42\n7\n9\n3\n10\n4\n",
            """
            struct Address { var city: String }
            struct Point { var x: Int, var y: Int }
            class User {
                var address: Address?
                init(address: Address?) { self.address = address }
            }
            class Node {
                var v: Int
                var next: Node?
                init(v: Int, next: Node?) { self.v = v; self.next = next }
            }
            fn city(_ user: User?) -> String { return user?.address?.city ?? "unknown" }
            fn plusOne(_ x: Int!) -> Int { return x + 1 }
            """,
            """
                print(city(User(address: Address { city: "Prague" })))
                print(city(User(address: none)))
                let f: ((Int) -> Int)? = (x: Int) => x * 2
                print(f?(21) ?? 0)
                let g: (() -> Int)? = none
                print(g?() ?? 7)
                let a = Node(v: 1, next: Node(v: 2, next: none))
                a.next?.v = 9
                print(a.next?.v ?? 0)
                a.next?.next = Node(v: 3, next: none)
                print(a.next?.next?.v ?? 0)
                var p: Point? = Point { x: 1, y: 2 }
                p?.x = 10
                print(p?.x ?? 0)
                print(plusOne(3))
            """
        );
}
