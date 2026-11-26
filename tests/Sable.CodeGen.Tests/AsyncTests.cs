using System.Globalization;
using System.Text.RegularExpressions;
using Sable.SIL;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     async and await (docs/02 §2.7, docs/04 §5): async functions are
///     state machines whose frames are on their task's slab, and tasks run
///     on a pool of threads. A test of how tasks interleave where they wait
///     runs natively with no pool (<see cref="Programs.OneThread" />), where
///     the interleaving is deterministic and the interpreter's is the native
///     one. Actors serialise their calls; calls into one from outside it
///     await, and hop onto it.
/// </summary>
[Native]
public sealed class AsyncTests {
    const string Declarations = """
                                actor Counter {
                                    var count = 0
                                    fn bump() -> Int {
                                        count = count + 1
                                        return count
                                    }
                                }

                                fn work(_ n: Int) async -> Int {
                                    await Task.yield()
                                    return n * 2
                                }

                                fn worker(_ name: String, _ rounds: Int, _ counter: Counter) async -> Int {
                                    var last = 0
                                    for i in 0..<rounds {
                                        print("\(name) \(i)")
                                        last = await counter.bump()
                                        await Task.yield()
                                    }
                                    return last
                                }
                                """;

    static string AsyncProgram(string declarations, string body) =>
        $"module Test\n{declarations}\n@main\nfn main() async {{\n{body}\n}}\n";

    [Fact]
    public void TasksInterleaveWhereTheyWait() =>
        Assert.Equal(
            "43\na 0\nb 0\na 1\nb 1\na 2\ntotal 11 count 7\nlate 2\n",
            AssertAgree(AsyncProgram(Declarations, """
                    let c = Counter()
                    let x = await c.bump()
                    async let y = work(21)
                    let z = await y
                    print("\(x + z)")
                    let a = Task.spawn { await worker("a", 3, c) }
                    let b = Task.spawn { await worker("b", 2, c) }
                    let total = await a.value + await b.value
                    print("total \(total) count \(await c.bump())")
                    let late = Task.spawn { "late \(await work(1))" }
                    print(await late.value)
                """), environment: OneThread).Stdout
        );

    // An actor's stored property is read from outside it by waiting, as a call is (#123).
    [Fact]
    public void AnActorsPropertyIsReadByWaitingForTheActor() =>
        Assert.Equal(
            "1 1\n",
            AssertAgree(AsyncProgram(Declarations, """
                    let c = Counter()
                    let bumped = await c.bump()
                    print("\(bumped) \(await c.count)")
                """)).Stdout
        );

    [Fact]
    public void ATaskNobodyAwaitsStillRunsBeforeTheProgramEnds() =>
        Assert.Equal(
            "main\nbackground 1\nbackground 2\n",
            AssertAgree(AsyncProgram("", """
                    let ignored = Task.spawn {
                        print("background 1")
                        await Task.yield()
                        print("background 2")
                        return 0
                    }
                    print("main")
                """), environment: OneThread).Stdout
        );

    [Fact]
    public void ATaskWhoseBodyGivesNothingCanBeAwaited() =>
        // A Task<Void>: its result is a Void?, whose 'Some' carries nothing.
        Assert.Equal(
            "spawned\nhi\nafter\nquiet\n",
            AssertAgree(AsyncProgram("", """
                    let t = Task.spawn { print("hi") }
                    print("spawned")
                    await t.value
                    print("after")
                    let unawaited = Task.spawn { print("quiet") }
                """), environment: OneThread).Stdout
        );

    /// <summary>
    ///     A sleeping task is off the run queue: two tasks sleeping at once take
    ///     as long as the longer, not the sum, and wake in the order of their
    ///     deadlines (#185).
    /// </summary>
    [Fact]
    public void SleepingTasksSleepTogether() =>
        Assert.Equal(
            "short\nlong\n3\ntrue\n",
            AssertAgree(AsyncProgram("import Sable.Time", """
                    let start = Instant.now()
                    let long = Task.spawn {
                        try? await Task.sleep(nanoseconds: 400_000_000)
                        print("long")
                        return 1
                    }
                    let short = Task.spawn {
                        try? await Task.sleep(nanoseconds: 300_000_000)
                        print("short")
                        return 2
                    }
                    print(await long.value + await short.value)
                    let took = start.elapsed
                    print(took >= Duration.milliseconds(400) && took < Duration.milliseconds(650))
                """)).Stdout
        );

    /// <summary>
    ///     Cancelling is cooperative: the task sees it through
    ///     Task.isCancelled, and a sleep it is in, or starts, ends at once
    ///     and throws CancellationError — two ten-second sleeps take no time
    ///     (#185).
    /// </summary>
    [Fact]
    public void ACancelledTaskSeesItAndStopsSleeping() =>
        Assert.Equal(
            "false\nstarted\nwoke true: the task was cancelled\n1\ntrue\n2\nfalse\n",
            AssertAgree(AsyncProgram("import Sable.Time\nimport Sable.Concurrency", """
                    let start = Instant.now()
                    print(Task.isCancelled)
                    let sleeper = Task.spawn {
                        print("started")
                        do {
                            try await Task.sleep(nanoseconds: 10_000_000_000)
                        } catch {
                            print("woke \(Task.isCancelled): \(error.description())")
                        }
                        return 1
                    }
                    await Task.yield()
                    sleeper.cancel()
                    sleeper.cancel()
                    print(await sleeper.value)

                    // Cancelled before it starts: it sees it from the start, and does not sleep.
                    let (both, count) = await withTaskGroup(of: Bool.self) { group in
                        group.cancelAll()
                        group.spawn {
                            let slept = (try? await Task.sleep(nanoseconds: 10_000_000_000)) != none
                            return !slept && Task.isCancelled
                        }
                        group.spawn { Task.isCancelled }
                        var all = true
                        var count = 0
                        for await cancelled in group {
                            all = all && cancelled
                            count += 1
                        }
                        return (all, count)
                    }
                    print(both)
                    print(count)
                    print(start.elapsed > Duration.seconds(5))
                """)).Stdout
        );

    /// <summary>
    ///     A task is a frame on a slab of its own, not a thread: twenty
    ///     thousand tasks run on the program's one thread (#185, #276).
    /// </summary>
    [Fact]
    public void ManyTasksDoNotNeedAThreadEach() =>
        Assert.Equal(
            "199990000\n",
            AssertAgree(AsyncProgram("", """
                    var tasks: Task<Int>[>..] = []
                    for i in 0..<20000 {
                        tasks.append(Task.spawn { i })
                    }
                    var sum = 0
                    for task in tasks {
                        sum += await task.value
                    }
                    print(sum)
                """), stepLimit: 50_000_000).Stdout
        );

    [Fact]
    public void ATrapInATaskStopsTheProgram() {
        var execution = AssertAgree(AsyncProgram("", """
                let failing = Task.spawn {
                    let values: Int[>..] = []
                    return values[3]
                }
                print("before")
                print(await failing.value)
            """), environment: OneThread);
        Assert.Equal("before\n", execution.Stdout);
        Assert.Equal("fatal error: array index out of range\n  at test.sb:7:16\n", execution.Stderr);
    }

    [Theory]
    [InlineData("    let c = Counter()\n    c.bump()", "'bump', isolated to its actor, is async, so the expression needs 'await'")]
    [InlineData("    let t = Task.spawn { 1 }\n    print(t.value)", "reading 'value' is async, so the expression needs 'await'")]
    [InlineData("    async let y = work(1)\n    print(y)", "'y', an 'async let', is async, so the expression needs 'await'")]
    [InlineData("    let c = Counter()\n    print(c.count)", "reading 'count', isolated to the actor 'Counter', is async, so the expression needs 'await'")]
    [InlineData("    let c = Counter()\n    c.count = 3", "'count' is the state of the actor 'Counter', which only it changes")]
    public void AwaitIsWrittenWhereATaskMayWait(string body, string message) {
        var result = Pipeline.Compile(new SourceText(AsyncProgram(Declarations, body), "test.sb"));
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    /// <summary>'var x: T { async get { … } }': a getter that may wait, as Task's 'value' is declared.</summary>
    [Fact]
    public void AnAsyncGetterWaitsWhereItsTaskMay() =>
        Assert.Equal(
            "a\ngot 42\n",
            AssertAgree(AsyncProgram(
                """
                class Later {
                    var n: Int = 41
                    var value: Int {
                        async get {
                            await Task.yield()
                            return n + 1
                        }
                    }
                }
                """,
                """
                    let later = Later()
                    let other = Task.spawn {
                        print("a")
                        return 0
                    }
                    let got = await later.value
                    let zero = await other.value
                    print("got \(got + zero)")
                """)).Stdout
        );

    /// <summary>
    ///     An await chain, recursive so that nothing inlines it, each level
    ///     waiting while another task runs: what each suspends and resumes
    ///     with, every value live across an await, is kept in frames on the
    ///     task's slab, so a thousand rounds allocate no more than ten (#276).
    /// </summary>
    static string AwaitChain(int rounds) =>
        AsyncProgram(
            """
            fn chain(_ depth: Int, _ label: String) async -> Int {
                if depth == 0 {
                    await Task.yield()
                    return label.count
                }
                let below = await chain(depth - 1, label)
                return below + depth
            }
            """,
            $$"""
                let other = Task.spawn {
                    var turns = 0
                    for _ in 0..<{{rounds}} {
                        await Task.yield()
                        turns += 1
                    }
                    return turns
                }
                var total = 0
                for round in 0..<{{rounds}} {
                    total += await chain(6, "chain")
                }
                print("\(total) \(await other.value)")
            """);

    [Fact]
    public void AnAwaitAllocatesNothing() {
        Assert.Equal("260 10\n", AssertAgree(AwaitChain(10)).Stdout);
        Assert.Equal("26000 1000\n", AssertAgree(AwaitChain(1000), stepLimit: 50_000_000).Stdout);
        if (!CanBuild) {
            return;
        }

        static (string Allocations, string Chunks) Counts(string stats) =>
            (Regex.Match(stats, @"allocations (\d+),").Groups[1].Value, Regex.Match(stats, @"frame chunks (\d+)").Groups[1].Value);

        var stats = new Dictionary<string, string> { ["SABLE_STATS"] = "1" };
        var few = Counts(BuildAndRun(AwaitChain(10), environment: stats).Stderr);
        var many = Counts(BuildAndRun(AwaitChain(1000), environment: stats).Stderr);
        Assert.NotEqual("", few.Allocations);
        Assert.Equal(few, many);
        // chain(6) down to chain(0) are seven frames on main's slab, as a
        // function that calls itself is inlined nowhere (once, chain(6)
        // was inlined into main, and six fitted): one chunk past the
        // slab's first, made once and kept for every round after.
        Assert.Equal("1", many.Chunks);
    }

    /// <summary>
    ///     A sleeping task is its frames and a timer: a hundred thousand of
    ///     them, each sleeping a second, take about a second and little memory
    ///     each — a thread each would be 800 GB of stacks (#185, #276).
    ///     A task is about a kilobyte: the task itself, 896 bytes with the
    ///     first 576 of its frames inside (tasks.c, frames.c), so its root
    ///     frame and the sleep's take no other memory; its closure and its
    ///     handle, a hundred bytes from the slabs; and eight bytes each in
    ///     the sleepers' heap and the array of handles. It peaks at about
    ///     110 MB, the bound's 256 MiB less than half.
    /// </summary>
    [Fact]
    [Trait("Category", "Timing")]
    public void AHundredThousandSleepingTasksTakeASecondInBoundedMemory() {
        if (!CanBuild) {
            return;
        }

        var run = BuildAndRun(
            AsyncProgram("import Sable.Time", """
                    let start = Instant.now()
                    var tasks: Task<Int>[>..] = []
                    for i in 0..<100_000 {
                        tasks.append(Task.spawn {
                            try? await Task.sleep(nanoseconds: 1_000_000_000)
                            return i % 2
                        })
                    }
                    var odd = 0
                    for task in tasks {
                        odd += await task.value
                    }
                    let took = start.elapsed
                    print(odd)
                    print(took >= Duration.seconds(1) && took < Duration.milliseconds(1500))
                """),
            environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" });
        Assert.Equal("50000\ntrue\n", run.Stdout);
        var peak = long.Parse(Regex.Match(run.Stderr, @"peak memory (\d+) KiB").Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(peak, 1, 256 * 1024);
    }

    /// <summary>Task.settle() goes on once no other task can, however many threads there are, and one at a time.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("8")]
    public void SettlingWaitsUntilNoOtherTaskCanGoOn(string workers) {
        if (!CanBuild) {
            return;
        }

        var program = AsyncProgram("", """
                let steps = Atomic(0)
                let never = WaitList()
                for i in 0..<20 {
                    let _ = Task.detached {
                        for _ in 0..<200 {
                            await Task.yield()
                        }
                        let child = Task.spawn {
                            await Task.yield()
                            let _ = steps.update { $0 + 1 }
                        }
                        await child.value
                        let _ = steps.update { $0 + 1 }
                        if i % 2 == 0 {
                            await never.park()
                        }
                    }
                }
                await Task.settle()
                print(steps.load())

                // From other tasks, each alone.
                let running = Atomic(0)
                let most = Atomic(0)
                var settlers: Task<Void>[>..] = []
                for _ in 0..<8 {
                    settlers.append(Task.spawn {
                        for _ in 0..<50 {
                            await Task.settle()
                            let now = running.update { $0 + 1 } + 1
                            let _ = most.update { $0 > now ? $0 : now }
                            let _ = running.update { $0 - 1 }
                        }
                    })
                }
                for settler in settlers {
                    await settler.value
                }
                print(most.load())
                await Task.settle()
                print("done")
            """);
        Assert.Equal("40\n1\ndone\n", BuildAndRun(program, environment: new Dictionary<string, string> { ["SABLE_WORKERS"] = workers }).Stdout);
        Assert.Equal("40\n1\ndone\n", AssertAgree(program).Stdout);
    }

    /// <summary>A task still settling as main returns goes on, and only then: main running is a task that can go on.</summary>
    [Fact]
    public void ATaskSettlingGoesOnAsMainReturns() =>
        Assert.Equal("main\nsettled\n", AssertAgree(AsyncProgram("", """
                let _ = Task.detached {
                    await Task.settle()
                    print("settled")
                }
                Runtime.sleep(nanoseconds: 5_000_000)
                print("main")
            """)).Stdout);

    /// <summary>A closure that awaits, over a type parameter's values, is async in generic code compiled once, throwing or not.</summary>
    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void AnAsyncClosureInGenericCodeWaits(GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        Assert.Equal("7\n", AssertAgree(AsyncProgram("""
                protocol Waiter: Sendable {
                    fn wait() async throws(CancellationError)
                }
                struct Napper: Waiter {
                    fn wait() async throws(CancellationError) {
                        try await Task.sleep(nanoseconds: 1_000_000)
                    }
                }
                fn later<W: Waiter>(_ waiter: W, _ value: Int) -> Task<Int> {
                    return Task.detached {
                        try? await waiter.wait()
                        return value
                    }
                }
            """, "    print(await later(Napper(), 7).value)")).Stdout);
    }

    /// <summary>Of two overloads that differ in whether a closure is async, the one the closure is written for.</summary>
    [Fact]
    public void AClosureThatAwaitsChoosesTheAsyncOverload() =>
        Assert.Equal("sync\nasync\nasync\nsync\n", AssertAgree(AsyncProgram("""
                fn run(_ body: () -> Int) -> String {
                    let _ = body()
                    return "sync"
                }
                fn run(_ body: async () -> Int) async -> String {
                    let _ = await body()
                    return "async"
                }
                fn one() async -> Int { return 1 }
            """, """
                print(run { 1 })
                print(await run { await one() })
                print(await run {
                    var total = 0
                    for _ in 0..<2 { total += await one() }
                    return total
                })
                print(run {
                    let nested: async () -> Int = { await one() }
                    return 2
                })
            """)).Stdout);

    /// <summary>A requirement with a default is not implemented by a type's overload of another requirement of its name.</summary>
    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void AnOverloadOfAnotherRequirementDoesNotStandInForADefault(GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        Assert.Equal("until 3\nuntil 10\nuntil 10\nfor 1\n", AssertAgree("""
            module Test
            protocol Sleeper {
                fn sleep(until moment: Int)
                fn sleep(for length: String)
            }
            extension Sleeper {
                fn sleep(for length: String) { sleep(until: 10) }
            }
            struct Plain: Sleeper {
                fn sleep(until moment: Int) { print("until \(moment)") }
            }
            struct Both: Sleeper {
                fn sleep(until moment: Int) { }
                fn sleep(for length: String) { print("for \(length)") }
            }
            fn go<S: Sleeper>(_ s: S, _ length: String) { s.sleep(for: length) }
            @main
            fn main() {
                Plain().sleep(until: 3)
                go(Plain(), "1")
                let boxed: any Sleeper = Plain()
                boxed.sleep(for: "1")
                go(Both(), "1")
            }
            """).Stdout);
    }

    /// <summary>An initialiser of a constrained extension settles the type parameters of the type it makes.</summary>
    [Fact]
    public void AConstrainedExtensionsInitialiserSettlesItsType() =>
        Assert.Equal("5\n9\n", AssertAgree("""
            module Test
            struct Holder<T> {
                let value: T
                init(value: T) { self.value = value }
            }
            extension Holder where T == Int {
                init(doubled: Bool = false) { self.init(value: doubled ? 10 : 5) }
            }
            @main
            fn main() {
                let plain = Holder()
                print(plain.value)
                print(Holder(value: 9).value)
            }
            """).Stdout);
}
