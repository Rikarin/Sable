using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     docs/04 §3.7: objects that do not outlive their frame are made in it.
///     Nothing observable changes — the deinit runs at the last release, in
///     order, and nothing leaks — only where the memory came from.
/// </summary>
public sealed class StackPromotionTests {
    [Fact]
    public void APromotedObjectIsDeinitedWhereItWouldHaveBeen() =>
        Assert.Equal(
            "made a\nmade b\nsum 3\nbye b\nbye a\nmade c\nescaped c\nafter\nbye c\n",
            AssertAgree(
                Program(
                    """
                    class Noisy {
                        var name: String
                        var value: Int
                        init(_ name: String, _ value: Int) {
                            self.name = name
                            self.value = value
                            print("made " + name)
                        }
                        deinit {
                            print("bye " + name)
                        }
                    }

                    fn sum(_ x: Noisy, _ y: Noisy) -> Int {
                        return x.value + y.value
                    }

                    fn local() {
                        let a = Noisy("a", 1)
                        let b = Noisy("b", 2)
                        print("sum \(sum(a, b))")
                    }

                    fn escaping() -> Noisy {
                        let c = Noisy("c", 3)
                        print("escaped " + c.name)
                        return c
                    }
                    """,
                    """
                        local()
                        let kept = escaping()
                        print("after")
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     In a loop, an object gone by the end of each turn, a closure only
    ///     called, and an array literal only read or grown in place are made
    ///     in the frame, one place reused turn after turn: the deinits run as
    ///     before, an array grown past its literal moves its elements to the
    ///     heap, and nothing is allocated but that.
    /// </summary>
    [Fact]
    public void ObjectsClosuresAndArraysMadeInALoopReuseTheFrame() {
        var source = Program(
            """
            class Noisy {
                var name: String
                init(_ name: String) {
                    self.name = name
                }
                deinit {
                    print("bye " + name)
                }
            }

            fn apply(_ f: (Int) -> Int, _ x: Int) -> Int {
                return f(x)
            }

            fn sum(_ values: Int[>..]) -> Int {
                var total = 0
                for v in values {
                    total = total + v
                }
                return total
            }
            """,
            """
                var total = 0
                for i in 0..<3 {
                    let n = Noisy("n\(i)")
                    total = total + apply({ x in x * i }, i) + n.name.count
                    var grown: Int[>..] = [i, total]
                    grown.append(1)
                    let names = [n.name, "x"]
                    total = total + sum([i, i]) + sum(grown) + names.count
                }
                print(total)
                var count = 0
                for i in 0..<1000 {
                    let add = { (d: Int) in count = count + d }
                    add(i % 2)
                }
                print(count)
            """
        );
        Assert.Equal("bye n0\nbye n1\nbye n2\n73\n500\n", AssertAgree(source).Stdout);
    }

    /// <summary>
    ///     #188's measure, scaled down: each turn's closure, object and array
    ///     literal (and the box a captured loop variable had) were each an
    ///     allocation, 500,001 for 100,000 turns; now there is only the one
    ///     box the second loop's closure shares, made before it.
    /// </summary>
    [Fact]
    public void ALoopOfClosuresObjectsAndArraysAllocatesNothing() {
        var source = Program(
            """
            class Point {
                var x: Int
                var y: Int
                init(_ x: Int, _ y: Int) {
                    self.x = x
                    self.y = y
                }
            }

            fn apply(_ f: (Int) -> Int, _ x: Int) -> Int {
                return f(x)
            }
            """,
            """
                var total = 0
                for i in 0..<10000 {
                    total = total + apply({ x in x + i }, i)
                    let p = Point(i, 1)
                    let a = [i, p.y, 2]
                    total = total + p.x + a.count + a[1]
                }
                print(total)
                var count = 0
                for i in 0..<10000 {
                    let add = { (d: Int) in count = count + d }
                    add(i % 2)
                }
                print(count)
            """
        );
        Assert.Equal("150025000\n5000\n", AssertAgree(source, stepLimit: 50_000_000).Stdout);
        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("allocations 1,", stats, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnInlineValueOutgrowingItsFrameMovesToTheHeap() =>
        Assert.Equal(
            "200 1\n",
            AssertAgree(
                Program(
                    """
                    @inline_layout
                    struct Bag {
                        var items: U8[>..]
                    }

                    fn fill() -> String {
                        var bag = Bag { items: [] }
                        for i in 0..<200 {
                            bag.items.append(U8(i % 7))
                        }
                        var ones = 0
                        for item in bag.items {
                            if item == 1 {
                                ones = ones + 1
                            }
                        }
                        return "\(bag.items.count) \(ones / 29)"
                    }
                    """,
                    "    print(fill())"
                )
            ).Stdout
        );
}
