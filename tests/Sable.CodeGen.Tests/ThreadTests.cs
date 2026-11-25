using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Concurrency's threads, locals and parallel loops: the interpreter,
///     which runs a thread to its end as it is started and a loop on the one
///     thread it has, and the native code agree where the program does not ask
///     which thread does what; and what traps.
/// </summary>
[Native]
public sealed class ThreadTests {
    [Fact]
    public void TheInterpreterAndTheNativeCodeAgree() {
        var run = AssertAgree("""
            module P
            import Sable.Concurrency
            import Sable.Time

            struct Refused: Error { let code: Int }

            class Marked {
                let name: String
                init(_ name: String) { self.name = name }
                deinit { print("gone \(name)") }
            }

            @main
            fn main() async {
                let thread = Thread.spawn(name: "worker") { Thread.current.name ?? "-" }
                print(thread.join())
                print(thread.thread.name ?? "-")
                print(await thread.value())
                print(thread.isFinished)
                do {
                    _ = try Thread.spawn { () throws(Refused) -> Int in throw Refused { code: 7 } }.join()
                } catch {
                    print(error.code)
                }
                print(Thread.current.name ?? "-")

                var sum = 0
                var text = ""
                let numbers = [1, 2, 3]
                Thread.scope { s in
                    s.spawn { sum = numbers.reduce(0) { $0 + $1 } }
                    s.spawn { text = "\(numbers.count)" }
                }
                print("\(sum) \(text)")

                let first = ThreadLocal { Marked("first") }
                let second = ThreadLocal { Marked("second") }
                Thread.spawn {
                    _ = first.value
                    _ = second.value
                }.join()
                let count = ThreadLocal { 40 }
                count.value += 1
                print(count.withValue { $0 += 1; return $0 })

                let request = TaskLocal("none")
                let seen = await request.withValue("bound") {
                    await Task.spawn { request.value }.value
                }
                print("\(seen) \(request.value)")

                Thread.current.unpark()
                Thread.park()
                print(Thread.park(atMost: Duration.milliseconds(1)))

                let xs = (0..<1000).toArray()
                print(xs.parallel.map { $0 * 2 }.parallel.filter { $0 % 100 == 0 }.count)
                print(xs.parallel.sum())
                print(xs.parallel.first { $0 > 500 } ?? -1)
                var ys = xs.parallel.sorted(by: { $0 > $1 })
                ys.parallelModify { $0 -= 1 }
                print("\(ys[0]) \(ys[999])")
                let total = Atomic(0)
                parallelFor(0..<10) { i in
                    parallelFor(0..<10) { j in total.add(i * j) }
                }
                print(total.load())
                do {
                    try parallelFor(0..<10) { (i: Int) throws(Refused) -> Void in
                        if i == 3 {
                            throw Refused { code: 3 }
                        }
                    }
                } catch {
                    print(error.code)
                }
                print(Thread.blockOn { await Task.spawn { 42 }.value })
                try! await parallelForEach(1...4, degree: 2) { n in total.add(n) }
                print(total.load())
            }
            """);
        Assert.Equal(
            "worker\nworker\nworker\ntrue\n7\nmain\n6 3\ngone second\ngone first\n42\nbound none\nfalse\n20\n499500\n501\n998 -1\n2025\n3\n42\n2035\n",
            run.Stdout
        );
    }

    // The main thread's values go as the program ends, before its globals.
    [Fact]
    public void TheMainThreadsLocalsAreDestroyedAsTheProgramEnds() {
        var run = AssertAgree("""
            module P
            import Sable.Concurrency

            class Marked {
                let name: String
                init(_ name: String) { self.name = name }
                deinit { print("gone \(name)") }
            }

            let first: ThreadLocal<Marked> = ThreadLocal { Marked("first") }
            let second: ThreadLocal<Marked> = ThreadLocal { Marked("second") }

            @main
            fn main() {
                _ = first.value
                _ = second.value
                print("ends")
            }
            """);
        Assert.Equal("ends\ngone second\ngone first\n", run.Stdout);
    }

    // A program ends once the threads it started have: with no pool, too.
    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void TheProgramWaitsForItsThreads(string workers) {
        if (!CanBuild || !NativeMode.Wanted) {
            return;
        }

        var run = BuildAndRun("""
            module P
            import Sable.Concurrency
            import Sable.Time
            @main
            fn main() {
                Thread.spawn {
                    Thread.sleep(Duration.milliseconds(20))
                    print("thread")
                    _ = Task.spawn {
                        await Task.yield()
                        print("task")
                    }
                }.detach()
                print("main")
            }
            """, environment: new Dictionary<string, string> { ["SABLE_WORKERS"] = workers });
        Assert.Equal("main\nthread\ntask\n", run.Stdout);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void ATrapOnAThreadStopsTheProgram() {
        if (!CanBuild || !NativeMode.Wanted) {
            return;
        }

        var run = BuildAndRun("""
            module P
            import Sable.Concurrency
            @main
            fn main() {
                let xs = [1]
                Thread.spawn { xs[xs.count] }.join()
                print("not reached")
            }
            """);
        Assert.Equal("", run.Stdout);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("out of range", run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let h = Thread.spawn { 1 }\n    h.detach()\n    h.join()", "joined that was detached")]
    [InlineData("var kept: ThreadScope? = none\n    Thread.scope { s in kept = s }\n    kept!.spawn { }", "spawned within it")]
    [InlineData("let local = ThreadLocal { 0 }\n    local.withValue { $0 = local.value }", "while 'withValue' has it")]
    [InlineData("parallelFor(0..<4, degree: -1) { i in }", "cannot be run by -1 threads")]
    [InlineData("var xs = [1, 2]\n    xs.parallelModifyChunks(of: 1) { chunk in chunk[1] = 0 }", "out of range for a chunk of 1")]
    public void AMisuseTraps(string body, string message) {
        var run = AssertAgree($"module P\nimport Sable.Concurrency\n@main\nfn main() {{\n    {body}\n}}\n");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(message, run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerDoesNotBlockOnATask() {
        if (!CanBuild || !NativeMode.Wanted) {
            return;
        }

        var run = BuildAndRun("""
            module P
            import Sable.Concurrency
            @main
            fn main() async {
                print(await Task.spawn { Thread.blockOn { 1 } }.value)
            }
            """, environment: new Dictionary<string, string> { ["SABLE_WORKERS"] = "2" });
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("await it instead", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInterpreterTrapsOnAParkNoOtherThreadCouldEnd() {
        var (_, trap) = Interpret("""
            module P
            import Sable.Concurrency
            @main
            fn main() {
                Thread.spawn { Thread.park() }.join()
            }
            """);
        Assert.NotNull(trap);
        Assert.Contains("parks with no token", trap, StringComparison.Ordinal);
    }

    // A var that a thread's closure captures is in a box both reach; what the
    // thread leaves there is counted as shared.
    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    public void WhatThreadsAndLoopsShareIsCountedSo(string workers) {
        if (!CanBuild || !NativeMode.Wanted) {
            return;
        }

        var run = BuildAndRun("""
            module P
            import Sable.Concurrency

            class Node {
                var next: Node? = none
                let text: String
                init(_ text: String) { self.text = text }
            }

            struct Holder: Sendable { var texts: String[>..] }

            @main
            fn main() {
                var holder = Holder { texts: ["a"] }
                let kept = holder
                for round in 0..<10 {
                    Thread.scope { s in
                        s.spawn {
                            for i in 0..<100 {
                                holder.texts.append("text \(i) of \(round)")
                            }
                        }
                        s.spawn { (0..<100).parallel.sum { $0 + kept.texts.count } }
                    }
                }
                let lengths = holder.texts.parallel.map { $0 + "!" }.parallel.sum { $0.count }
                print("\(holder.texts.count) \(kept.texts.count) \(lengths)")
                let local = ThreadLocal { Node("n") }
                Thread.spawn { local.value.next = Node("m") }.join()
                print(local.value.text)
            }
            """, environment: new Dictionary<string, string> { ["SABLE_WORKERS"] = workers, ["SABLE_CHECK_SHARED"] = "1" });
        Assert.Equal("1001 1 12902\nn\n", run.Stdout);
        Assert.Equal(0, run.ExitCode);
    }
}
