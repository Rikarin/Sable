using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     docs/04 §3.7: objects that do not outlive their frame are made in it.
///     Nothing observable changes — the deinit runs at the last release, in
///     order, and nothing leaks — only where the memory came from.
/// </summary>
[Native]
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
    /// <summary>
    ///     An object in the frame that nothing copies is never counted: its
    ///     one reference ends in a destroy that runs its deinit where it is,
    ///     with no release (#188). One copied is counted as before, and an
    ///     inline value that outgrew its frame is released from the heap.
    /// </summary>
    [Fact]
    public void AnObjectInTheFrameThatNothingCopiesIsNotCounted() {
        var source = Program(
            """
            class Noisy {
                var value: Int
                init(_ value: Int) {
                    self.value = value
                }
                deinit {
                    if value % 250 == 0 {
                        print(value)
                    }
                }
            }

            @inline_layout
            struct Line {
                var sku: U8[..8]
                var qty: U16
            }

            @inline_layout
            struct Order {
                var lines: Line[>..]
            }

            fn total(_ order: Order) -> Int {
                var t = 0
                for line in order.lines {
                    t = t + Int(line.qty)
                }
                return t
            }

            fn priced(_ i: Int) -> Int {
                var order = Order { lines: [] }
                order.lines.append(Line { sku: "A", qty: U16(i % 5) })
                order.lines.append(Line { sku: "B", qty: 1 })
                return total(order)
            }
            """,
            """
                var sum = 0
                for i in 0..<1000 {
                    let n = Noisy(i)
                    sum = sum + n.value + priced(i)
                }
                print(sum)
            """
        );
        Assert.Equal("0\n250\n500\n750\n502500\n", AssertAgree(source, stepLimit: 50_000_000).Stdout);

        var sil = Pipeline.Compile(new SourceText(source, "test.sb"), optimised: true).Sil!;
        Assert.Contains(sil.Functions.SelectMany(static f => f.Blocks).SelectMany(static b => b.Instructions),
            static i => i is DestroyValueInst { Uncounted: true } or DestroyAddrInst { Uncounted: true });
        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("retains 0, releases 0", stats, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     A closure whose context is in the frame, given to a callee that
    ///     keeps it no longer than the call, and never copied: its destroy is
    ///     the context's, uncounted, so a call of it in a loop neither
    ///     retains nor releases (#246) — and what the context holds is still
    ///     released, once, as it goes.
    /// </summary>
    [Fact]
    public void AClosureWhoseContextNothingCopiesIsNotCounted() {
        var source = Program(
            """
            class Box {
                var value: Int
                init(_ value: Int) {
                    self.value = value
                }
                deinit {
                    print("bye \(value)")
                }
            }

            @inline(never)
            fn apply(_ x: Int, _ body: (Int) -> Int) -> Int {
                return body(x)
            }
            """,
            """
                var sum = 0
                for i in 0..<1000 {
                    sum = sum + apply(i) { $0 + i }
                }
                print(sum)
                let box = Box(7)
                print(apply(1) { $0 + box.value })
            """
        );
        Assert.Equal("999000\n8\nbye 7\n", AssertAgree(source).Stdout);

        var sil = Pipeline.Compile(new SourceText(source, "test.sb"), optimised: true).Sil!;
        Assert.Contains(sil.Functions.SelectMany(static f => f.Blocks).SelectMany(static b => b.Instructions),
            static i => i is DestroyValueInst { Uncounted: true, Value: PartialApplyInst });
        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            // The box's, captured, and the printed lines': none of the loop's thousand closures.
            Assert.Matches(@"retains 1, releases [0-9]\b", stats);
        }
    }

    /// <summary>
    ///     A loop body of several blocks — an 'if' in it, a local assigned
    ///     another object in one branch — makes its objects and array in
    ///     the frame too, when each is gone on every path back to where it
    ///     is made (#188): nothing is allocated, and the deinits run as
    ///     before.
    /// </summary>
    [Fact]
    public void ALoopBodyOfSeveralBlocksMakesItsObjectsInTheFrame() {
        var source = Program(
            """
            class Point {
                var x: Int
                var y: Int
                init(_ x: Int, _ y: Int) {
                    self.x = x
                    self.y = y
                }
                deinit {
                    if x % 2500 == 0 {
                        print(x)
                    }
                }
            }
            """,
            """
                var total = 0
                for i in 0..<10000 {
                    let p = Point(i, 1)
                    if i % 3 == 0 {
                        total = total + p.x
                    } else {
                        total = total - p.y
                    }
                    let a = [i, p.y, 2]
                    var q = Point(i, 2)
                    if i % 2 == 0 {
                        q = Point(i + 1, 3)
                    }
                    total = total + a.count + a[1] + q.y
                }
                print(total)
            """
        );
        Assert.Equal("0\n0\n2500\n2500\n5000\n5000\n7500\n7500\n16726667\n", AssertAgree(source, stepLimit: 50_000_000).Stdout);
        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("allocations 0,", stats, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     An object in the frame that nothing counts and nothing reads the
    ///     header of — a callee only reaching its fields, a closure's code
    ///     only its context's — is made with no header (#188): the runtime
    ///     neither makes nor destroys it, and its deinit and its fields'
    ///     releases still run where they did. One handed to a callee that
    ///     copies it, which counts it, or built with debug information,
    ///     which the debugger reads, keeps its header.
    /// </summary>
    [Fact]
    public void AnObjectNothingReadsTheHeaderOfIsMadeWithNone() {
        var source = Program(
            """
            class Noisy {
                var value: Int
                var name: String
                init(_ value: Int, _ name: String) {
                    self.value = value
                    self.name = name
                }
                deinit {
                    if value % 250 == 0 {
                        print("bye \(name) \(value)")
                    }
                }
            }

            @inline(never)
            fn read(_ n: Noisy) -> Int {
                return n.value + n.name.count
            }

            @inline(never)
            fn apply(_ x: Int, _ body: (Int) -> Int) -> Int {
                return body(x)
            }

            @inline(never)
            fn copied(_ n: Noisy) -> Int {
                var kept = n
                if n.value > 1000000 {
                    kept = Noisy(0, "z")
                }
                return kept.name.count
            }

            @inline(never)
            fn dispatched(_ i: Int) -> Int {
                let n = Noisy(i, "d")
                return copied(n)
            }
            """,
            """
                var sum = 0
                for i in 0..<1000 {
                    let n = Noisy(i, "n")
                    let label = "k\(i % 10)"
                    sum = sum + read(n) + apply(i) { $0 + i + label.count } + dispatched(i)
                }
                print(sum)
            """
        );
        const string expected = "bye d 0\nbye n 0\nbye d 250\nbye n 250\nbye d 500\nbye n 500\nbye d 750\nbye n 750\n1502500\n";
        Assert.Equal(expected, AssertAgree(source, stepLimit: 50_000_000).Stdout);

        static string Body(string ir, string function) {
            var start = ir.IndexOf($"@\"sable.Test.{function}\"(", StringComparison.Ordinal);
            start = ir.LastIndexOf("define", start, StringComparison.Ordinal);
            return ir[start..ir.IndexOf("\n}\n", start, StringComparison.Ordinal)];
        }

        var compiled = Pipeline.Compile(new SourceText(source, "test.sb"), optimised: true);
        var ir = compiled.LlvmIr!;
        Assert.DoesNotContain("@sable_stack_object(", Body(ir, "main"), StringComparison.Ordinal);
        Assert.DoesNotContain("@sable_destroy_uncounted(", Body(ir, "main"), StringComparison.Ordinal);
        Assert.Contains("@sable_stack_object(", Body(ir, "dispatched"), StringComparison.Ordinal);
        Assert.Contains("@sable_stack_object(", Body(LlvmEmitter.EmitWithDebugInfo(compiled.Sil!, "."), "main"), StringComparison.Ordinal);
        if (CanBuild) {
            // Optimised, and held to leaking nothing.
            Assert.Equal(expected, BuildAndRun(source).Stdout);
        }
    }

    /// <summary>
    ///     A local function captures a 'let' that has its value where the
    ///     function is declared by value, as a closure does, with no box
    ///     (#188); its context, made each turn, is then in the frame.
    /// </summary>
    [Fact]
    public void ALocalFunctionCapturesALetByValue() {
        var source = Program(
            """
            fn run(_ n: Int) -> Int {
                let base = n * 2
                fn scaled(_ x: Int) -> Int {
                    return x * base
                }
                var total = 0
                for i in 0..<n {
                    let step = i % 3
                    fn bump() -> Int {
                        return step + base
                    }
                    total = total + scaled(i) + bump()
                }
                return total
            }
            """,
            """
                var sum = 0
                for k in 0..<200 {
                    sum = sum + run(k % 7)
                }
                print(sum)
            """
        );
        var sil = SilPrinter.Print(Pipeline.Compile(new SourceText(source, "test.sb")).Sil!);
        Assert.DoesNotContain("alloc_ref $Box$of$Int", sil, StringComparison.Ordinal);
        var output = AssertAgree(source, stepLimit: 50_000_000);
        Assert.Equal("15426\n", output.Stdout);
        if (CanBuild) {
            var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
            Assert.Contains("allocations 0,", stats, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     An array literal borrowed across an await is in no frame: the one
    ///     that made it is gone while the task waits.
    /// </summary>
    [Fact]
    public void AnArrayLiteralOutlivesASuspension() =>
        Assert.Equal(
            "a one\nb two\n",
            Programs.AssertAgree(
                """
                module P

                protocol Named: Sendable {
                    async fn named() throws
                    async fn name() -> String
                }

                extension Named {
                    async fn named() throws { }
                }

                @immutable
                final class One: Named {
                    async fn name() -> String { return "one" }
                }

                @immutable
                final class Two: Named {
                    async fn name() -> String { return "two" }
                }

                struct Entry: Sendable {
                    let key: String
                    let value: any Named
                }

                fn all(_ entries: Entry[>..]) async {
                    for entry in entries {
                        let value = entry.value
                        let task = Task.detached { await value.name() }
                        print("\(entry.key) \(await task.value)")
                    }
                }

                @main
                async fn main() {
                    await all([Entry { key: "a", value: One() }, Entry { key: "b", value: Two() }])
                }
                """
            ).Stdout
        );

    /// <summary>
    ///     What a callee copies out of its inout parameter it may keep: an
    ///     object, or an array, made for the caller's variable is not made in
    ///     the caller's frame then.
    /// </summary>
    [Fact]
    public void WhatACalleeKeepsOfAnInoutParameterEscapes() =>
        Assert.Equal(
            "gone 0\n8\n1\ngone 1\n9\n2\n10\n3\ngone 3\ngone 2\n",
            Programs.AssertAgree(
                """
                module InoutKeepsFrameObject

                // What an inout parameter is given is moved or copied out of it by the
                // callee: an object made in the caller's frame, that it may keep, is not.

                final class Node {
                    var n: Int

                    init(_ n: Int) {
                        self.n = n
                    }

                    deinit {
                        print("gone \(n)")
                    }
                }

                struct Wrap {
                    var node: Node
                }

                @inline(never)
                fn steal(_ source: inout Node, _ target: inout Node) {
                    target = source
                }

                @inline(never)
                fn stealPart(_ source: inout Wrap, _ target: inout Node) {
                    target = source.node
                }

                @inline(never)
                fn stealArray(_ source: inout Node[>..], _ target: inout Node[>..][>..]) {
                    target.append(source)
                }

                @inline(never)
                fn make(_ target: inout Node) {
                    var x = Node(1)
                    steal(&x, &target)
                }

                @inline(never)
                fn makePart(_ target: inout Node) {
                    var x = Wrap { node: Node(2) }
                    stealPart(&x, &target)
                }

                @inline(never)
                fn makeArray(_ target: inout Node[>..][>..]) {
                    var x: Node[>..] = [Node(3)]
                    stealArray(&x, &target)
                }

                @inline(never)
                fn smash(_ k: Int) -> Int {
                    var x: Int[>..] = [k, k, k, k, k, k, k, k]
                    x[0] = 7
                    return x[0] + x[k]
                }

                fn main() {
                    var a = Node(0)
                    make(&a)
                    print(smash(1))
                    print(a.n)
                    makePart(&a)
                    print(smash(2))
                    print(a.n)
                    var all: Node[>..][>..] = []
                    makeArray(&all)
                    print(smash(3))
                    print(all[0][0].n)
                }
                """
            ).Stdout
        );
}
