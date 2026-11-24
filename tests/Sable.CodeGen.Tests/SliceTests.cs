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
            // Not even the array: a literal of constants is one, in read-only memory.
            Assert.Contains("allocations 0,", stats, StringComparison.Ordinal);
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
    ///     Loops indexing a slice up to its count, reading and writing: the
    ///     optimiser's versioned loops take a slice's count for the bound, as
    ///     they take an array's.
    /// </summary>
    [Fact]
    public void IndexedLoopsOverASlice() =>
        Prints(
            "9\n[1, 2, 3, 4, 5] [4, 6, 8]\n",
            "",
            """
                let xs: Int[>..] = [1, 2, 3, 4, 5]
                var s = xs[1..<4]
                var total = 0
                var i = 0
                while i < s.count {
                    total = total + s[i]
                    i = i + 1
                }
                print(total)
                var j = 0
                while j < s.count {
                    s[j] = s[j] * 2
                    j = j + 1
                }
                print("\(xs) \(s)")
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
        Assert.Equal("fatal error: array index out of range\n  at test.sb:8:11\n", execution.Stderr);
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
        Assert.Equal("fatal error: array index out of range\n  at test.sb:9:5\n", execution.Stderr);
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
            "12\n7\n2\n",
            """
            @inline_layout
            struct Line {
                var sku: U8[..16]
                var qty: U16
            }

            @inline_layout
            struct Order {
                var id: U64
                var codes: U16[..8]
                var lines: Line[>..]
            }

            fn total(_ xs: U16[:]) -> Int {
                var sum = 0
                for x in xs { sum = sum + Int(x) }
                return sum
            }

            fn quantities(_ lines: Line[:]) -> Int {
                var sum = 0
                for line in lines { sum = sum + Int(line.qty) }
                return sum
            }
            """,
            """
                var order = Order { id: 7, codes: [3, 4, 5], lines: [] }
                order.lines.append(Line { sku: "a", qty: 2 })
                order.lines.append(Line { sku: "bb", qty: 5 })
                print(total(order.codes))
                print(quantities(order.lines))
                print(order.lines.count)
            """
        );


    const string Swap = """
                        extension<Element> Element[:] {
                            mutating fn swapEnds() {
                                let first = self[0]
                                self[0] = self[self.count - 1]
                                self[self.count - 1] = first
                            }

                            mutating fn swapPair(_ i: Int, _ j: Int) {
                                let held = self[i]
                                self[i] = self[j]
                                self[j] = held
                            }
                        }
                        """;

    /// <summary>
    ///     A mutating member of <c>Element[:]</c> changes the array it is
    ///     called on, where it is, whatever its kind and wherever it is kept:
    ///     a local, a field of an object or a struct, an inout parameter, and
    ///     <c>self</c> in a kind's own extension.
    /// </summary>
    [Fact]
    public void AMutatingSliceMemberChangesTheArray() =>
        Prints(
            "[3, 2, 1] [6, 5] [9, 7, 8]\n[\"c\", \"b\", \"a\"]\n[2, 1] [4, 3]\n[1, 2, 3]\n4\n[6, 5]\n[3, 2, 1] [1, 2]\n",
            Swap + """

                   extension<Element> Element[>..] {
                       mutating fn swapEndsTwice() {
                           self.swapEnds()
                           swapEnds()
                       }
                   }

                   class Box {
                       var items: Int[>..] = [1, 2]
                   }

                   struct Pair {
                       var items: Int[>..<]
                   }

                   fn swapped(_ xs: inout Int[>..]) {
                       xs.swapEnds()
                   }
                   """,
            """
                var grow: Int[>..] = [1, 2, 3]
                grow.swapEnds()
                var shrink: Int[>..<] = [5, 6]
                shrink.swapEnds()
                var fixed: Int[] = [8, 7, 9]
                fixed.swapEnds()
                print("\(grow) \(shrink) \(fixed)")
                var words: String[>..] = ["a", "b", "c"]
                words.swapEnds()
                print("\(words)")
                let box = Box()
                box.items.swapEnds()
                var pair = Pair { items: [3, 4] }
                pair.items.swapEnds()
                print("\(box.items) \(pair.items)")
                grow.swapEndsTwice()
                swapped(&grow)
                print("\(grow)")
                shrink.append(4)
                print(shrink.removeLast())
                print("\(shrink)")
                grow.swapPair(0, grow.count - 1)
                box.items.swapPair(box.items.count - 2, box.items.count - 1)
                print("\(grow) \(box.items)")
            """
        );

    /// <summary>
    ///     The array is changed in place: viewed whole, its buffer changes
    ///     where it is and comes back with nothing copied — the one allocation
    ///     is the array's own. A buffer another array shares is copied by the
    ///     first write, so the other keeps what it had.
    /// </summary>
    [Fact]
    public void AMutatingSliceMemberWritesInPlace() {
        var source = Program(
            Swap,
            """
                var xs: Int[>..] = [1, 2, 3, 4, 5]
                var k = 0
                while k < 101 {
                    xs.swapEnds()
                    k = k + 1
                }
                let kept = xs
                xs.swapEnds()
                print(xs[0])
                print(kept[0])
            """
        );
        Assert.Equal("1\n5\n", AssertAgree(source).Stdout);

        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("allocations 2,", stats, StringComparison.Ordinal);
        }
    }

    const string Inout = """
                         struct Model { var items: Int[>..] }
                         fn bump(_ s: inout Int[:]) {
                             s.swapAt(0, 1)
                             s[0] = s[0] * 10
                         }
                         fn sortAll<T: Comparable>(_ s: inout T[:]) { s.sort() }
                         fn show<T>(_ xs: T[:]) -> String {
                             var text = ""
                             var first = true
                             for x in xs {
                                 text += first ? "\(x)" : " \(x)"
                                 first = false
                             }
                             return text
                         }
                         """;

    /// <summary>
    ///     The whole of an array passed as an inout slice, which changes it
    ///     where it is: every kind of array, a field's, an object's, an
    ///     element's, to a declared function and a closure, and one whose
    ///     buffer is shared, the other's kept.
    /// </summary>
    [Fact]
    public void TheWholeOfAnArrayPassesAsAnInoutSlice() =>
        Prints(
            """
            10 3 2
            3
            2 3 10
            40 5
            7 8 9
            20 1
            20 1
            1 2
            3 4
            a b
            77 3 10 3

            0

            """,
            Inout + """

                    class Box { var xs: Int[>..]; init(_ xs: Int[>..]) { self.xs = xs } }
                    fn nested(_ s: inout Int[:]) { bump(&s) }
                    fn first(_ s: inout Int[:], _ value: Int) { s[0] = value }
                    """,
            """
                var xs: Int[>..] = [3, 1, 2]
                let kept = xs
                bump(&xs)
                print(show(xs))
                print(kept[0])
                sortAll(&xs)
                print(show(xs))
                var fixed: Int[] = [5, 4]
                bump(&fixed)
                print(show(fixed))
                var shrinking: Int[>..<] = [9, 8, 7]
                sortAll(&shrinking)
                print(show(shrinking))
                var model = Model(items: [1, 2])
                nested(&model.items)
                print(show(model.items))
                let box = Box([1, 2])
                bump(&box.xs)
                print(show(box.xs))
                var bounded: Int[..4] = [2, 1]
                sortAll(&bounded)
                print(show(bounded))
                var grid: Int[>..][>..] = [[2, 1], [4, 3]]
                sortAll(&grid[1])
                print(show(grid[1]))
                var words: String[>..] = ["b", "a"]
                sortAll(&words)
                print(show(words))
                let change = { (s: inout Int[:]) in s[0] = 77 }
                change(&xs)
                first(&xs, xs[0])
                print("\(show(xs)) \(xs.count)")
                var empty: Int[>..] = []
                sortAll(&empty)
                print(show(empty))
                print(empty.count)
            """
        );

    [Fact]
    public void TheWholeOfAnArrayGivenBackWithAnotherCountTraps() {
        var execution = AssertAgree(
            Program(
                Inout + """

                        fn shorten(_ s: inout Int[:]) { s = s[0..<1] }
                        """,
                """
                    var xs: Int[>..] = [1, 2]
                    shorten(&xs)
                    print("unreachable")
                """
            )
        );
        Assert.StartsWith("fatal error: a slice of another count given back for the whole of an array\n", execution.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A range of an array passed inout (#301): every kind of array, by
    ///     every shape of range and one counted from the end — read out as a
    ///     slice, changed by the callee and stored back over the range.
    /// </summary>
    [Fact]
    public void ARangeOfEveryKindOfArrayPassesInout() =>
        Prints(
            """
            1 30 2 4 5
            1 20 30 4 5
            1 300 20 4 5
            1 200 300 4 5
            1 200 300 50 4
            2000 1 300 50 4
            10 2000 300 50 4
            10 2000 300 40 50
            1 30 40 2 5
            300 1 2 4 5
            20 1 3 4 5
            c a d b
            1 30 2 4

            """,
            Inout,
            """
                var grow: Int[>..] = [1, 2, 3, 4, 5]
                bump(&grow[1..<4])
                print(show(grow))
                bump(&grow[1...3])
                print(show(grow))
                bump(&grow[1..<^1])
                print(show(grow))
                bump(&grow[^4...^2])
                print(show(grow))
                bump(&grow[3...])
                print(show(grow))
                bump(&grow[..<2])
                print(show(grow))
                bump(&grow[...1])
                print(show(grow))
                bump(&grow[^2...])
                print(show(grow))
                var shrink: Int[>..<] = [1, 2, 3, 4, 5]
                bump(&shrink[1..<4])
                bump(&shrink[^3...])
                print(show(shrink))
                var fixed: Int[] = [1, 2, 3, 4, 5]
                bump(&fixed[1..<4])
                bump(&fixed[..<^3])
                print(show(fixed))
                var sized: Int[5] = [1, 2, 3, 4, 5]
                bump(&sized[0..<2])
                print(show(sized))
                var names: String[>..] = ["d", "c", "b", "a"]
                sortAll(&names[0..<2])
                sortAll(&names[^2...])
                sortAll(&names[1..<3])
                print(show(names))
                var held: Int[>..] = [1, 2, 3, 4]
                let range = 1..<3
                bump(&held[range])
                print(show(held))
            """
        );

    /// <summary>
    ///     A range of a range, of a struct's field, of an inout slice; a
    ///     mutating member of a slice called on a range; an element of a
    ///     range stored to; and the array another shares is copied by the
    ///     write, so the other keeps what it had.
    /// </summary>
    [Fact]
    public void RangesOfRangesFieldsAndSlicesPassInout() =>
        Prints(
            """
            1 2 40 3 5 6
            1 30 2 4
            5 2 3 4 1
            5 2 3 1 4
            42 2 3
            300 1 2 / 1 30 2
            a c b d

            """,
            Inout + """

                    fn inner(_ s: inout Int[:]) { bump(&s[1..<3]) }
                    """,
            """
                var nested: Int[>..] = [1, 2, 3, 4, 5, 6]
                bump(&nested[1..<5][1..<3])
                print(show(nested))
                var model = Model { items: [1, 2, 3, 4] }
                bump(&model.items[1..<3])
                print(show(model.items))
                var xs: Int[>..] = [5, 4, 3, 2, 1]
                xs[1..<4].sort()
                print(show(xs))
                xs[^2...].reverse()
                print(show(xs))
                var parts: Int[] = [1, 2, 3]
                parts[0..<2][0] = 42
                print(show(parts))
                var shared: Int[>..] = [1, 2, 3]
                bump(&shared[^2...])
                let kept = shared
                bump(&shared[0..<2])
                print("\(show(shared)) / \(show(kept))")
                var strings: String[>..] = ["a", "b", "c", "d"]
                strings[1..<3].reverse()
                print(show(strings))
            """
        );

    /// <summary>
    ///     A store to a range of an array that does not grow, or of a slice,
    ///     is element by element (#293 refused it); a growable array's range
    ///     is replaced, as many elements as are stored.
    /// </summary>
    [Fact]
    public void ARangeIsStoredTo() =>
        Prints(
            """
            1 8 9 4
            1 8 1 8
            1 7 4
            0 0 3

            """,
            Inout + """

                    fn clear(_ s: inout Int[:]) { s[0..<2] = [0, 0] }
                    """,
            """
                var fixed: Int[] = [1, 2, 3, 4]
                fixed[1..<3] = [8, 9]
                print(show(fixed))
                fixed[^2...] = fixed[0..<2]
                print(show(fixed))
                var grow: Int[>..] = [1, 2, 3, 4]
                grow[1..<3] = [7]
                print(show(grow))
                var small: Int[>..] = [1, 2, 3]
                clear(&small[0...])
                print(show(small))
            """
        );

    /// <summary>An array literal of integers is of Int before another type, as a bare literal is.</summary>
    [Fact]
    public void AnArrayLiteralChoosesTheOverloadOfItsOwnElements() =>
        Prints(
            "ints 3\nfloats 2\nints 2\nbytes 1\n",
            """
                    fn took(_ xs: Int[:]) { print("ints \(xs.count)") }
                    fn took(_ xs: F64[:]) { print("floats \(xs.count)") }
                    fn narrow(_ xs: U8[:]) { print("bytes \(xs.count)") }
                    fn narrow(_ xs: String[:]) { print("texts \(xs.count)") }
                    """,
            """
                took([1, 9, 90])
                took([1.5, 2.0])
                took([Int.max, 2])
                narrow([7])
            """
        );

    /// <summary>
    ///     A whole array, of any kind, passed as an inout slice is its range
    ///     from end to end: changed where it is, a copy of it untouched.
    /// </summary>
    [Fact]
    public void AWholeArrayIsPassedAsAnInoutSlice() =>
        Prints(
            """
            0 0 3 / 1 2 3
            0 0 5 6
            0 0 8
            0 0 9

            """,
            Inout + """

                    fn clear(_ s: inout Int[:]) { s[0..<2] = [0, 0] }
                    struct Holder { var items: Int[>..] }
                    """,
            """
                var grow: Int[>..] = [1, 2, 3]
                let kept = grow
                clear(&grow)
                print("\(show(grow)) / \(show(kept))")
                var fixed: Int[4] = [1, 2, 5, 6]
                clear(&fixed)
                print(show(fixed))
                var holder = Holder { items: [6, 7, 8] }
                clear(&holder.items)
                print(show(holder.items))
                var nested: Int[>..][>..] = [[7, 8, 9]]
                clear(&nested[0])
                print(show(nested[0]))
            """
        );

    /// <summary>A whole array given back with another count traps, as a range of it does.</summary>
    [Fact]
    public void AWholeArrayGivenBackWithAnotherCountTraps() {
        var execution = AssertAgree(
            Program(
                Inout + """

                        fn shorten(_ s: inout Int[:]) { s = s[0..<1] }
                        """,
                "    var xs: Int[>..] = [1, 2, 3, 4]\n    shorten(&xs)\n    print(\"unreachable\")"
            )
        );
        Assert.StartsWith("fatal error: a slice of count 1 stored over a range of count 4\n", execution.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A callee can give its slice another count only by replacing it
    ///     whole; stored back over the range, that traps, as a store of
    ///     another count to a fixed array's range does.
    /// </summary>
    [Fact]
    public void ARangeGivenBackWithAnotherCountTraps() {
        var execution = AssertAgree(
            Program(
                Inout + """

                        fn shorten(_ s: inout Int[:]) { s = s[0..<1] }
                        """,
                """
                    var xs: Int[>..] = [1, 2, 3, 4]
                    shorten(&xs[0..<1])
                    print(show(xs))
                    shorten(&xs[1..<4])
                    print("unreachable")
                """
            )
        );
        Assert.Equal("1 2 3 4\n", execution.Stdout);
        Assert.StartsWith("fatal error: a slice of count 1 stored over a range of count 3\n", execution.Stderr, StringComparison.Ordinal);

        var fixedStore = AssertAgree(Program("", "    var fixed: Int[] = [1, 2, 3]\n    fixed[0..<2] = [9]\n    print(\"unreachable\")"));
        Assert.StartsWith("fatal error: a slice of count 1 stored over a range of count 2\n", fixedStore.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The range's view is the array's buffer: a callee that only reads
    ///     copies nothing, and one that writes copies the run once, stored
    ///     back in place.
    /// </summary>
    [Fact]
    public void ARangePassedInoutCopiesOnlyWhatIsWritten() {
        var source = Program(
            Inout + """

                    fn peek(_ s: inout Int[:]) -> Int => s[0]
                    """,
            """
                var xs: Int[>..] = []
                for i in 0..<8 { xs.append(i) }
                var total = 0
                var k = 0
                while k < 10 {
                    total += peek(&xs[2..<6])
                    k += 1
                }
                bump(&xs[2..<6])
                print(total)
                print(xs[2])
                print(xs[3])
            """
        );
        Assert.Equal("20\n30\n2\n", AssertAgree(source).Stdout);

        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            // The array's own, and the one copy of the run 'bump' writes.
            Assert.Contains("allocations 2,", stats, StringComparison.Ordinal);
        }
    }
}
