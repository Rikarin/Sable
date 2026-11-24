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

    /// <summary>
    ///     A field set through a subscript, 'a[i].x = v': the element read
    ///     out, changed and written back — taken when it holds references,
    ///     loaded as plain bits when it is trivial, as a generic struct is
    ///     once its parameter is a trivial type (it was taken there, which
    ///     the SIL verifier refuses).
    /// </summary>
    [Fact]
    public void AFieldIsSetThroughASubscriptOfAnyElement() =>
        Prints(
            "5 2\n7 9\nb 1\n",
            """
            struct Point {
                var x: Int
                var y: Int
            }

            struct Node<Value> {
                var value: Value
                var next: Int
            }

            struct Named {
                var name: String
                var count: Int
            }

            fn link<Value>(_ nodes: inout Node<Value>[>..], _ at: Int, _ next: Int) {
                nodes[at].next = next
            }
            """,
            """
                var points: Point[>..] = [Point { x: 1, y: 2 }]
                points[0].x = 5
                print("\(points[0].x) \(points[0].y)")
                var nodes: Node<Int>[>..] = [Node<Int> { value: 7, next: -1 }]
                link(&nodes, 0, 9)
                print("\(nodes[0].value) \(nodes[0].next)")
                var named: Named[>..] = [Named { name: "a", count: 0 }]
                named[0].name = "b"
                named[0].count += 1
                print("\(named[0].name) \(named[0].count)")
            """
        );

    /// <summary>
    ///     An extension of every growable array of a kind declares
    ///     initialisers, whose self starts as '[]'; one throws part way,
    ///     and what it had appended goes.
    /// </summary>
    [Fact]
    public void AGrowableArraysExtensionDeclaresInitialisers() =>
        Prints(
            "[7, 7, 7]\n[\"a\", \"a\"]\n[1, 2, 3]\nfull at 2\n0\n",
            """
            struct Full: Error { let at: Int }

            extension<E> E[>..] {
                init(repeating value: E, times: Int) {
                    for _ in 0..<times {
                        append(value)
                    }
                }

                init(upTo limit: Int, _ values: E[:]) throws(Full) {
                    for value in values {
                        if count == limit {
                            throw Full { at: limit }
                        }
                        self.append(value)
                    }
                }
            }

            extension<E> E[>..<] {
                init(twice value: E) {
                    append(value)
                    append(value)
                }
            }

            protocol Empty {
                init()
            }

            extension<E> E[>..]: Empty { }

            fn makeEmpty<T: Empty>() -> T {
                return T()
            }
            """,
            """
                print(Int[>..](repeating: 7, times: 3))
                print(String[>..<](twice: "a"))
                do {
                    print(try Int[>..](upTo: 5, [1, 2, 3]))
                    print(try String[>..](upTo: 2, ["a", "b", "c"]))
                } catch {
                    print("full at \(error.at)")
                }
                let empty: String[>..] = makeEmpty()
                print(empty.count)
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

    /// <summary>
    ///     Arrays are Equatable, Hashable and Comparable where their elements
    ///     are: through a generic function, in a dictionary's keys, and with
    ///     the operators written out, for every kind and a slice.
    /// </summary>
    [Fact]
    public void ArraysCompareAndHashAsTheirElementsDo() =>
        Prints(
            "true false true true\ntrue false true true\ntrue false\n2 1\n",
            """
            fn same<T: Equatable>(_ a: T, _ b: T) -> Bool {
                return a == b
            }

            fn first<T: Comparable>(_ a: T, _ b: T) -> Bool {
                return a < b
            }

            fn key<T: Hashable>(_ a: T) -> Int {
                return a.hashValue
            }
            """,
            """
                let words: String[>..] = ["a", "b"]
                let fixed: Int[3] = [1, 2, 3]
                let nested: Int[>..][>..] = [[1], [2, 3]]
                let other: Int[>..][>..] = [[1], [2, 4]]
                print("\(same(words, ["a", "b"])) \(same(words, ["a"])) \(same(fixed[1...], [2, 3])) \(same(nested, nested))")
                print("\(first(nested, other)) \(first(other, nested)) \(words < ["b"]) \(fixed >= [1, 2, 3])")
                print("\(key(words) == key(["a", "b"] as String[])) \(key(nested) == key(other))")
                var counts: [String[>..]: Int] = [:]
                counts[words] = 2
                counts[["b"]] = 1
                print("\(counts[["a", "b"]] ?? 0) \(counts[["b"]] ?? 0)")
            """);

    /// <summary>
    ///     'Element[>..](repeating:count:)' and 'append(repeating:count:)',
    ///     for every element type: bytes and other trivial values copied as
    ///     bytes, and strings, structs holding them, arrays and a type
    ///     parameter's values copied as appending each would copy it.
    /// </summary>
    [Fact]
    public void RepeatingFillsCopiesOfAnyElement() =>
        Prints(
            "[258, 258, 258]\n41 7 1\n[\"hi\", \"hi\"]\nn 1\n[[1, 2], [1, 2]]\n[\"g\", \"g\", \"g\"] [3.5]\n[4, 4]\n41 43\n",
            """
            struct P {
                var x: I32
                var name: String
            }

            fn filled<T>(_ value: T, _ n: Int) -> T[>..] {
                var xs = T[>..](repeating: value, count: n)
                xs.append(repeating: value, count: 1)
                return xs
            }
            """,
            """
                print(Int[>..](repeating: 258, count: 3))
                var b: U8[>..] = [1]
                b.append(repeating: 7, count: 40)
                print("\(b.count) \(b[40]) \(b[0])")
                print(String[>..](repeating: "hi", count: 2))
                let p = P[>..](repeating: P { x: 1, name: "n" }, count: 2)
                print("\(p[1].name) \(p[0].x)")
                print(Int[>..][>..](repeating: [1, 2], count: 2))
                print("\(filled("g", 2)) \(filled(3.5, 0))")
                var shrink = Int[>..<](repeating: 4, count: 2)
                shrink.append(repeating: 0, count: 0)
                print(shrink)
                let shared = b
                b.append(repeating: 9, count: 2)
                print("\(shared.count) \(b.count)")
            """);

    [Fact]
    public void RepeatingANegativeCountTraps() {
        var execution = AssertAgree(Program("", "    var xs: Int[>..] = []\n    let n = -1\n    xs.append(repeating: 0, count: n)\n    print(xs.count)"));
        Assert.Contains("cannot append a negative number of elements", execution.Stderr, StringComparison.Ordinal);
    }
}
