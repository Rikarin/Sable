using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The array family's members, declared in Sable (lib/Sable.Core/Array.sb)
///     and an array extension of the program's own: native and interpreted
///     alike, leak-checked.
/// </summary>
public sealed class ArrayMemberTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void TheCoreMembersOfEachKind() =>
        Prints(
            "3 false\ntrue false\ntrue false\n2 1\n9 2\n0 true\n2 true\n",
            "",
            """
                var xs: Int[>..] = []
                xs.append(4)
                xs.append(element: 9)
                xs.append(1)
                print("\(xs.count) \(xs.isEmpty)")
                print("\(xs.contains(9)) \(xs.contains(element: 5))")
                var names: String[>..<] = ["ada", "grace"]
                print("\(names.contains("grace")) \(names.contains("alan"))")
                let part = xs.slice(from: 1, to: 3)
                print("\(part.count) \(part[1])")
                names.removeLast()
                print("\(xs[1]) \(names.count + 1)")
                xs.clear()
                print("\(xs.count) \(xs.isEmpty)")
                let fixed: String[] = ["a", "b"]
                print("\(fixed.count) \(fixed.contains("b"))")
            """
        );

    [Fact]
    public void BytesReadAsUtf8() =>
        Prints(
            "hé€!\n4\n",
            "",
            """
                let bytes: U8[>..] = [104, 195, 169, 226, 130, 172, 33]
                let text = bytes.toString()
                print(text)
                print(text.count)
            """
        );

    [Fact]
    public void AnArrayExtensionOfTheProgramsOwn() =>
        Prints(
            "9 pear\n9\n3\n",
            """
            struct Item { var name: String, var weight: Int }

            extension<T: Comparable> T[:] {
                fn largest() -> T {
                    var best = self[0]
                    for x in self {
                        if best < x {
                            best = x
                        }
                    }
                    return best
                }
            }

            extension<T> T[>..] {
                mutating fn pushTwice(_ item: T) {
                    append(item)
                    self.append(item)
                }
            }

            extension Item[>..] {
                fn totalWeight() -> Int {
                    var total = 0
                    for item in self {
                        total = total + item.weight
                    }
                    return total
                }
            }
            """,
            """
                var xs: Int[>..] = [3, 9, 4]
                let words: String[>..] = ["apple", "pear", "fig"]
                print("\(xs.largest()) \(words.largest())")
                var items: Item[>..] = [Item { name: "a", weight: 5 }]
                items.pushTwice(Item { name: "b", weight: 1 })
                xs.pushTwice(0)
                print(items.totalWeight() + items.count * 2 - xs.count + 1)
                print(items.count)
            """
        );

    /// <summary>
    ///     <c>capacity</c> and <c>reserveCapacity</c> (the <c>array_capacity</c>
    ///     and <c>array_reserve</c> builtins): room made up front, kept by
    ///     appends within it, and a reserve on a shared array copies it first.
    /// </summary>
    [Fact]
    public void CapacityAndReserve() =>
        Prints(
            "0 true\ntrue [1, 2]\ntrue\n[\"1\"] [\"1\", \"2\"]\n",
            "",
            """
                var xs: Int[>..] = []
                xs.reserveCapacity(0)
                print("\(xs.capacity) \(xs.isEmpty)")
                xs.reserveCapacity(40)
                xs.append(1)
                xs.append(2)
                let room = xs.capacity
                print("\(room >= 40) \(xs)")
                var i = 0
                while i < 38 {
                    xs.append(i)
                    i = i + 1
                }
                print(xs.capacity == room)
                var a: String[>..<] = ["1"]
                var b = a
                b.reserveCapacity(10)
                b.append("2")
                print("\(a) \(b)")
            """
        );

    /// <summary>
    ///     <c>rotate(from:to:left:)</c> (the <c>array_rotate</c> builtin): a
    ///     range of an array, or of a slice, rotated in place by moving the
    ///     elements — references neither retained nor released, so nothing
    ///     leaks and nothing is freed early — and copied first when shared.
    /// </summary>
    [Fact]
    public void RotateMovesARangeInPlace() =>
        Prints(
            "[\"a\", \"c\", \"d\", \"b\", \"e\"]\n[\"a\", \"d\", \"b\", \"c\", \"e\"]\n[2, 3, 1] [1, 2, 3]\n[5, 1, 2, 3, 4]\n[\"y\", \"z\", \"x\"]\n",
            "",
            """
                var words: String[>..] = ["a", "b", "c", "d", "e"]
                words.rotate(from: 1, to: 4, left: 1)
                print("\(words)")
                words.rotate(from: 1, to: 4, left: -2)
                words.rotate(from: 2, to: 2, left: 1)
                words.rotate(from: 0, to: 5, left: 10)
                print("\(words)")
                var xs: Int[>..<] = [1, 2, 3]
                let kept = xs
                xs.rotate(from: 0, to: 3, left: 1)
                print("\(xs) \(kept)")
                var fixed: Int[] = [1, 2, 3, 4, 5]
                fixed.rotate(left: 4)
                print("\(fixed)")
                let source: String[>..] = ["w", "x", "y", "z"]
                var view = source[1...]
                view.rotate(left: 1)
                print("\(view)")
            """
        );

    [Fact]
    public void RotatePastTheEndTraps() {
        var execution = AssertAgree(
            Program(
                "",
                """
                    var xs: Int[>..] = [1, 2, 3]
                    xs.rotate(from: 1, to: 4, left: 1)
                """
            )
        );
        Assert.Contains("fatal error: array slice out of range", execution.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>toArray()</c> (<c>array_of_slice</c> of an array or of a slice):
    ///     a whole array shares its buffer, a part of one is copied out.
    /// </summary>
    [Fact]
    public void ToArrayOfAnArrayAndOfASlice() {
        var source = Program(
            "",
            """
                var xs: Int[>..] = [1, 2, 3, 4]
                var all = xs.toArray()
                let part = xs[1..<3].toArray()
                all.append(5)
                xs[0] = 0
                print(xs.count + all.count * 10 + part[0] * 100 + part.count * 1000)
            """
        );
        Assert.Equal("2254\n", AssertAgree(source).Stdout);

        // The array; 'all', copied when it first grows, as it shared the
        // array's buffer until then; and 'part', copied out. 'xs' is written
        // in place, the only reference to its buffer by then.
        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("allocations 3,", stats, StringComparison.Ordinal);
        }
    }
}
