using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Every kind of array passed as a slice, and ranges of an array's
///     elements read out — natively and in the interpreter, which must agree.
/// </summary>
public sealed class SliceTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void ArraysAsSlicesAndRangesOfThem() =>
        Prints(
            "15\n30\n15\nann\n5\n12\n3\n9\n9\nbo\n",
            """
            fn sum(_ xs: Int[:]) -> Int {
                var total = 0
                for x in xs { total = total + x }
                return total
            }
            fn first<T>(_ xs: T[:]) -> T { return xs[0] }
            """,
            """
                let grow: Int[>..] = [1, 2, 3, 4, 5]
                let fixed: Int[] = [10, 20]
                var shrink: Int[>..<] = [7]
                shrink.append(8)
                print(sum(grow))
                print(sum(fixed))
                print(sum(shrink))
                let names: String[>..] = ["ann", "bo", "cy"]
                print(first(names))
                print(sum(grow[1..<3]))
                print(sum(grow[2...]))
                print(sum(grow[..<2]))
                print(sum(grow[1...3]))
                let r = 1..<4
                print(sum(grow[r]))
                print(first(names[1...]))
            """
        );

    const string Sum = """
                       fn sum(_ xs: Int[:]) -> Int {
                           var total = 0
                           for x in xs { total = total + x }
                           return total
                       }
                       """;

    /// <summary>
    ///     A slice is a view of the array's buffer: taking one, of an array
    ///     or of another slice, and passing an array where a slice is wanted,
    ///     allocate nothing — the one allocation is the array's own.
    /// </summary>
    [Fact]
    public void SlicingAllocatesNothing() {
        var source = Program(
            Sum,
            """
                let xs: Int[>..] = [1, 2, 3, 4, 5]
                var total = 0
                var k = 0
                while k < 100 {
                    let s = xs[1..<4]
                    total = total + sum(s) + sum(s[1...]) + sum(xs) + sum(xs.slice(from: 0, to: 2))
                    k = k + 1
                }
                print(total)
            """
        );
        Assert.Equal("3400\n", AssertAgree(source).Stdout);

        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("allocations 1,", stats, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A slice has value semantics, as the array it views does: a write
    ///     to the array after the slice was taken, or through the slice, copies
    ///     whichever buffer is shared, so neither sees the other's change.
    /// </summary>
    [Fact]
    public void WritesCopyTheSharedBuffer() =>
        Prints(
            "[1, 20, 3, 4, 5] [2, 3, 4]\n[1, 20, 3, 4, 5] [200, 3, 4]\n[1, 20, 3, 4, 5] [200, 4, 4]\n[200, 4, 4] [200, 9, 4]\n",
            """
            fn bump(_ x: inout Int) {
                x = x + 5
            }
            """,
            """
                var xs: Int[>..] = [1, 2, 3, 4, 5]
                var s = xs[1..<4]
                xs[1] = 20
                print("\(xs) \(s)")
                s[0] = 200
                print("\(xs) \(s)")
                s[1] = s[1] - 4
                bump(&s[1])
                print("\(xs) \(s)")
                let before = s
                s[1] = 9
                print("\(before) \(s)")
            """
        );

    /// <summary>
    ///     A slice keeps its buffer alive: one returned from the function whose
    ///     array it views, one in a struct, an object, a closure, an optional
    ///     and a tuple all outlive the array, and nothing leaks.
    /// </summary>
    [Fact]
    public void ASliceOutlivesItsArray() =>
        Prints(
            "[\"bo\", \"cy\"] 2\ncy\n9\n8\n[1, 2]\n[1]\n[2, 3] [4]\n",
            Sum + """

                  struct Window {
                      var rows: String[:]
                  }

                  class Holder {
                      var kept: Int[:]
                      init(_ kept: Int[:]) {
                          self.kept = kept
                      }
                  }

                  fn middle() -> String[:] {
                      let names: String[>..] = ["ann", "bo", "cy", "di"]
                      return names[1..<3]
                  }

                  fn counter() -> () -> Int {
                      let xs: Int[>..] = [1, 2, 3, 4, 5]
                      let s = xs[1..<3]
                      return { () -> Int in sum(s) + sum(s[1...]) }
                  }
                  """,
            """
                let m = middle()
                print("\(m) \(m.count)")
                let w = Window { rows: m }
                print(w.rows[1])
                var xs: Int[>..] = [1, 2, 3, 4, 5]
                let h = Holder(xs[3...])
                xs = []
                print(sum(h.kept))
                print(counter()())
                var ys: Int[>..] = [1, 2, 3]
                let o: Int[:]? = ys[..<2]
                let pair = (ys[0..<1], 5)
                ys.append(4)
                if let got = o { print("\(got)") }
                print("\(pair.0)")
                let again = ys[1...3]
                print("\(again.slice(from: 0, to: 2)) \(again[2...])")
            """
        );

    /// <summary>
    ///     An index into a slice is checked against the slice's own count,
    ///     not its buffer's: element 3 of a slice of three traps, though the
    ///     array it views has a fifth.
    /// </summary>
    [Fact]
    public void AnIndexPastTheSliceTraps() {
        var execution = AssertAgree(
            Program(
                "",
                """
                    let xs: Int[>..] = [1, 2, 3, 4, 5]
                    let s = xs[1..<4]
                    print(s[2])
                    print(s[3])
                """
            )
        );
        Assert.Equal("4\n", execution.Stdout);
        Assert.Equal("fatal error: slice index out of range\n  at test.sb:8:11\n", execution.Stderr);
    }

    /// <summary>A range of a slice is checked against the slice's count too.</summary>
    [Fact]
    public void ARangePastTheSliceTraps() {
        var execution = AssertAgree(
            Program(
                "",
                """
                    let xs: Int[>..] = [1, 2, 3, 4, 5]
                    let s = xs[1..<4]
                    print(s[1...2].count)
                    print(s[1...3].count)
                """
            )
        );
        Assert.Equal("2\n", execution.Stdout);
        Assert.Equal("fatal error: array slice out of range\n  at test.sb:8:11\n", execution.Stderr);
    }

    /// <summary>A write through a slice is checked against its count, before anything is copied.</summary>
    [Fact]
    public void AWritePastTheSliceTraps() {
        var execution = AssertAgree(
            Program(
                "",
                """
                    let xs: Int[>..] = [1, 2, 3, 4, 5]
                    var s = xs[1..<4]
                    s[2] = 0
                    print("\(s)")
                    s[3] = 0
                """
            )
        );
        Assert.Equal("[2, 3, 0]\n", execution.Stdout);
        Assert.Equal("fatal error: slice index out of range\n  at test.sb:9:5\n", execution.Stderr);
    }

    /// <summary>
    ///     Variadic arguments are a slice of the arguments gathered; a rest
    ///     pattern binds a slice of what it matched; an array's members that
    ///     every kind has take it as a slice.
    /// </summary>
    [Fact]
    public void VariadicsRestPatternsAndMembers() =>
        Prints(
            "9\n2 [3, 4]\nhi\nh\ntrue\nfalse\n",
            Sum + """

                  fn tail(_ xs: Int...) -> Int {
                      return sum(xs[1...])
                  }
                  """,
            """
                print(tail(1, 2, 3, 4))
                let xs: Int[>..] = [1, 2, 3, 4]
                match xs[1...] {
                    case [a, ...rest] => print("\(a) \(rest)")
                    case [] => print("empty")
                }
                let bytes: U8[>..] = [104, 105]
                print(bytes.toString())
                print(bytes[0..<1].toString())
                print(xs.contains(element: 3))
                print(xs[2...].contains(element: 2))
            """
        );

    /// <summary>
    ///     An array field of an <c>@inline_layout</c> struct passed as a slice:
    ///     its elements are bytes in the block, so they are copied out into a
    ///     buffer the slice views — scalars and inline structs alike.
    /// </summary>
    [Fact]
    public void AnInlineArrayFieldIsASlice() =>
        Prints(
            "294\n7\n2\n",
            """
            @inline_layout
            struct OrderLine {
                var sku: U8[..16]
                var qty: U16
                var price: U32
            }

            @inline_layout
            struct Order {
                var id: U64
                var codes: U8[..32]
                var lines: OrderLine[>..]
                var parent: Order&<u32>?
            }

            fn total(_ xs: U8[:]) -> Int {
                var sum = 0
                for x in xs { sum = sum + Int(x) }
                return sum
            }

            fn quantities(_ lines: OrderLine[:]) -> Int {
                var sum = 0
                for line in lines { sum = sum + Int(line.qty) }
                return sum
            }
            """,
            """
                var order = Order { id: 7, codes: "abc", lines: [], parent: none }
                order.lines.append(OrderLine { sku: "a", qty: 2, price: 1 })
                order.lines.append(OrderLine { sku: "bb", qty: 5, price: 1 })
                print(total(order.codes))
                print(quantities(order.lines))
                print(order.lines.count)
            """
        );
}
