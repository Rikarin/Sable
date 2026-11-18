using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     async and await (docs/02 §2.7, docs/04 §5): tasks scheduled
///     cooperatively, one at a time, switching only where one waits — so
///     the interleaving is deterministic, and the interpreter's is the
///     native one. Actors serialise by construction; calls into one from
///     outside it await.
/// </summary>
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
                """)).Stdout
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
                """)).Stdout
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
                """)).Stdout
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
                        await Task.sleep(nanoseconds: 400_000_000)
                        print("long")
                        return 1
                    }
                    let short = Task.spawn {
                        await Task.sleep(nanoseconds: 300_000_000)
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
    ///     Task.isCancelled(), and a sleep it is in, or starts, ends at once —
    ///     two ten-second sleeps take no time (#185).
    /// </summary>
    [Fact]
    public void ACancelledTaskSeesItAndStopsSleeping() =>
        Assert.Equal(
            "false\nstarted\nwoke true\n1\ntrue\n2\nfalse\n",
            AssertAgree(AsyncProgram("import Sable.Time\nimport Sable.Concurrency", """
                    let start = Instant.now()
                    print(Task.isCancelled())
                    let sleeper = Task.spawn {
                        print("started")
                        await Task.sleep(nanoseconds: 10_000_000_000)
                        print("woke \(Task.isCancelled())")
                        return 1
                    }
                    await Task.yield()
                    sleeper.cancel()
                    sleeper.cancel()
                    print(await sleeper.value)

                    // Cancelled before it starts: it sees it from the start, and does not sleep.
                    let group = TaskGroup<Bool>()
                    group.add(Task.spawn {
                        await Task.sleep(nanoseconds: 10_000_000_000)
                        return Task.isCancelled()
                    })
                    group.add(Task.spawn { Task.isCancelled() })
                    group.cancelAll()
                    let results = await group.waitForAll()
                    print(results[0] && results[1])
                    print(results.count)
                    print(start.elapsed > Duration.seconds(5))
                """)).Stdout
        );

    /// <summary>
    ///     A task is given a thread only when it first runs, and a finished
    ///     task's thread runs the next: twenty thousand tasks, which would be
    ///     twenty thousand threads, run on a few (#185).
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
            """));
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
}
