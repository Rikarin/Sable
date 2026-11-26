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

    /// <summary>
    ///     An inline array in every place a value can be: a union's payload,
    ///     an optional, a class's field, a closure's capture, a global, a
    ///     derived Equatable and Hashable struct, another inline array.
    /// </summary>
    [Fact]
    public void InlineArraysAreHeldEverywhereAValueIs() =>
        Prints(
            "4\n6\nw\nq\n30\n100\n50\ntrue\nfalse\n2\n11\n[1, 2, 3]\ntrue\n",
            """
            union Shape {
                Square(cells: Int[4])
                Empty
            }

            class Box {
                var cells: String[3]
                init(cells: String[3]) {
                    self.cells = cells
                }
            }

            @derive(Equatable, Hashable, Debug)
            struct Pair {
                var a: Int[2]
                var names: String[..2]
            }

            let table: Int[4] = [10, 20, 30, 40]

            fn total(_ xs: Int[:]) -> Int {
                var t = 0
                for x in xs {
                    t += x
                }
                return t
            }
            """,
            """
                let s = Shape.Square(cells: [1, 2, 3, 4])
                match s {
                    case .Square(cells: let xs) => print(xs[3])
                    case .Empty => print("empty")
                }
                var maybe: Int[4]? = none
                maybe = [5, 6, 7, 8]
                if let m = maybe {
                    print(m[1])
                }
                let box = Box(cells: ["x", "y", "z"])
                box.cells[1] = "w"
                print(box.cells[1])
                let captured: String[2] = ["p", "q"]
                let f = () => captured[1]
                print(f())
                print(table[2])
                print(total(table))
                print(total(table[1..<3]))
                let p = Pair { a: [1, 2], names: ["n"] }
                var q = p
                print(p == q)
                q.names.append("m")
                print(p == q)
                print(q.names.count)
                var grid: Int[2][2] = [[1, 2], [3, 4]]
                grid[1][0] = 9
                print(grid[1][0] + grid[0][1])
                let ys: Int[3] = [1, 2, 3]
                print(ys.toArray())
                print(p.hashValue == Pair { a: [1, 2], names: ["n"] }.hashValue)
            """
        );

    /// <summary>Generic code over one, which is specialised, an async function's frame, and reflection.</summary>
    [Fact]
    public void GenericAsyncAndReflectedInlineArrays() =>
        Assert.Equal(
            "a\n9\n103\nTagged(bytes: [1, 2, 3, 4])\nnone\n1 first 5\nCell struct 2\nCell(xs: [1, 2, 3], tag: [\"a\"])\nxs: Int[3] = [1, 2, 3]\n",
            AssertAgree("""
            module Test

            fn firstOf<T>(_ xs: T[3]) -> T {
                return xs[0]
            }

            fn largest<T: Comparable>(_ xs: T[:]) -> T {
                var best = xs[0]
                for x in xs {
                    if x > best {
                        best = x
                    }
                }
                return best
            }

            async fn later(_ xs: Int[4]) -> Int[4] {
                await Task.yield()
                var ys = xs
                ys[0] = 100
                return ys
            }

            @derive(Debug)
            struct Tagged {
                var bytes: U8[4]
            }

            struct Cell {
                var xs: Int[3]
                var tag: String[..2]
            }

            fn describe(_ xs: Int[..4]) -> String {
                if xs.count == 0 {
                    return "none"
                }
                return "\(xs.count) first \(xs[0])"
            }

            @main
            fn main() async {
                print(firstOf(["a", "b", "c"]))
                let xs: Int[4] = [3, 9, 2, 7]
                print(largest(xs))
                let ys = await later(xs)
                print(ys[0] + xs[0])
                let t = Tagged { bytes: [1, 2, 3, 4] }
                print(t.debugDescription())
                var b: Int[..4] = []
                print(describe(b))
                b.append(5)
                print(describe(b))
                let m = Mirror(of: Cell(xs: [1, 2, 3], tag: ["a"]))
                print("\(m.typeName) \(m.kind) \(m.count)")
                print(m.description)
                print("\(m.fields[0].name): \(m.fields[0].typeName) = \(m.fields[0].value)")
            }
            """).Stdout
        );

    /// <summary>
    ///     Made, read, written, gone through and lent to the members of
    ///     <c>T[:]</c> that read it or change it in place, an inline array
    ///     allocates nothing; only a view that is kept is copied out.
    /// </summary>
    [Fact]
    public void LentWhereItIsAndCopiedOnlyWhenKept() =>
        Prints(
            "0 0 0 0 0 0\n1\n[1, 9, 3, 4]\n9 9\n",
            """
            var kept: Int[:] = []

            fn keep(_ xs: Int[:]) {
                kept = xs
            }

            fn sum(_ xs: Int[:]) -> Int {
                var t = 0
                for x in xs {
                    t += x
                }
                return t
            }

            fn bump(_ xs: inout Int[:]) {
                xs[1] = 9
            }
            """,
            """
                var xs: Int[4] = [4, 2, 3, 1]
                var n = Runtime.allocationCount()
                xs[0] = 8
                let a = Runtime.allocationCount() - n
                n = Runtime.allocationCount()
                _ = sum(xs) + sum(xs[1..<3])
                let b = Runtime.allocationCount() - n
                n = Runtime.allocationCount()
                _ = xs.contains(element: 3)
                let c = Runtime.allocationCount() - n
                n = Runtime.allocationCount()
                xs.reverse()
                xs.swapAt(0, 3)
                let d = Runtime.allocationCount() - n
                n = Runtime.allocationCount()
                bump(&xs)
                let e = Runtime.allocationCount() - n
                n = Runtime.allocationCount()
                var bounded: Int[..4] = []
                bounded.append(1)
                bounded.append(2)
                _ = bounded.removeLast()
                let f = Runtime.allocationCount() - n
                print("\(a) \(b) \(c) \(d) \(e) \(f)")
                n = Runtime.allocationCount()
                keep(xs)
                print(Runtime.allocationCount() - n)
                xs = [1, 2, 3, 4]
                bump(&xs)
                print(xs.toArray())
                print("\(xs[1]) \(kept.count == 4 ? kept[1] : -1)")
            """
        );

    [Fact]
    public void AFullBoundedArrayTraps() {
        var execution = AssertAgree(Program("", """
                var xs: Int[..2] = []
                xs.append(1)
                xs.append(2)
                print(xs.count)
                xs.append(3)
            """));
        Assert.Equal("2\n", execution.Stdout);
        Assert.Contains("fatal error: inline array is full", execution.Stderr);
    }

    [Fact]
    public void AnIndexPastAnInlineArrayTraps() {
        var execution = AssertAgree(Program("", """
                var xs: Int[..4] = [1, 2]
                print(xs[1])
                print(xs[2])
            """));
        Assert.Equal("2\n", execution.Stdout);
        Assert.Contains("fatal error: array index out of range", execution.Stderr);
    }

    [Fact]
    public void ALiteralOfAnotherCountIsRefused() =>
        Assert.Contains(
            Pipeline.Compile(new SourceText(Program("", "let xs: Int[3] = [1, 2]"), "test.sb")).Errors,
            e => e.Contains("'Int[3]' holds exactly 3 elements, and this literal has 2", StringComparison.Ordinal));

    [Fact]
    public void AnInlineArrayTooLargeForTheStackIsRefused() =>
        Assert.Contains(
            Pipeline.Compile(new SourceText(Program("", "var xs: Int[..10000] = []\nxs.append(1)"), "test.sb")).Errors,
            e => e.Contains("S7004", StringComparison.Ordinal) && e.Contains("'Int[..10000]' takes 80008 bytes held inline", StringComparison.Ordinal));
}
