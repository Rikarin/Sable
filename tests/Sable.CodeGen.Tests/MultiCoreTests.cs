using System.Globalization;
using System.Text.RegularExpressions;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Tasks on a pool of threads, one per core (runtime/tasks.c, #276):
///     work spread over the cores, actors that serialise the calls of many
///     tasks running at once, channels between them, priorities that order
///     the run queues, and atomic counts on what tasks share — nothing lost,
///     nothing leaked. Every program here prints the same whatever the
///     interleaving, so the interpreter, on one thread, agrees with it.
/// </summary>
public sealed class MultiCoreTests {
    static string AsyncProgram(string declarations, string body) =>
        $"module Test\n{declarations}\n@main\nfn main() async {{\n{body}\n}}\n";

    const string Spin = """
                        fn spin(_ seed: Int, _ rounds: Int) -> Int {
                            var x = seed
                            for _ in 0..<rounds {
                                x = (x * 1103515245 + 12345) % 2147483648
                            }
                            return x
                        }
                        """;

    /// <summary>
    ///     A CPU-bound fan-out takes far less time on the pool than on one
    ///     worker: eight tasks of the same work, each on a core of its own.
    /// </summary>
    [Fact]
    [Trait("Category", "Timing")]
    public void AFanOutRunsInParallel() {
        if (!CanBuild || Environment.ProcessorCount < 4) {
            return;
        }

        var source = AsyncProgram("import Sable.Time\n" + Spin, """
                let start = Instant.now()
                var tasks: Task<Int>[>..] = []
                for i in 0..<8 {
                    tasks.append(Task.spawn { spin(i, 30_000_000) })
                }
                var total = 0
                for task in tasks {
                    total = (total + await task.value) % 1000003
                }
                print(total)
                print(start.elapsed.totalNanoseconds / 1_000_000)
            """);

        static (string Total, long Milliseconds) Run(string source, string workers) {
            var lines = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_WORKERS"] = workers }).Stdout.Split('\n');
            return (lines[0], long.Parse(lines[1], CultureInfo.InvariantCulture));
        }

        var one = Run(source, "1");
        var all = Run(source, Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(one.Total, all.Total);
        // Eight tasks on at least four cores: at least twice as fast.
        Assert.True(all.Milliseconds * 2 < one.Milliseconds, $"one worker {one.Milliseconds} ms, the pool {all.Milliseconds} ms");
    }

    /// <summary>
    ///     Sixteen tasks calling one actor at once: each call runs alone, so
    ///     no increment is lost, and every name the actor keeps is there.
    /// </summary>
    [Fact]
    public void AnActorCountsEveryCallOfManyTasks() =>
        Assert.Equal(
            "16000\n16000\n16\n",
            AssertAgree(AsyncProgram("""
                    actor Counter {
                        var count = 0
                        var seen: [String: Int] = [:]

                        fn bump(_ name: String) -> Int {
                            count += 1
                            seen[name] = (seen[name] ?? 0) + 1
                            return count
                        }

                        fn names() -> Int {
                            return seen.count
                        }
                    }
                    """, """
                    let counter = Counter()
                    var tasks: Task<Int>[>..] = []
                    for i in 0..<16 {
                        tasks.append(Task.spawn {
                            var last = 0
                            for _ in 0..<1000 {
                                last = await counter.bump("task \(i)")
                            }
                            return last
                        })
                    }
                    var most = 0
                    for task in tasks {
                        let last = await task.value
                        if last > most {
                            most = last
                        }
                    }
                    print(await counter.count)
                    print(most)
                    print(await counter.names())
                """), stepLimit: 50_000_000).Stdout
        );

    /// <summary>
    ///     An actor is reentrant where it waits: while one call is suspended
    ///     inside it, others run, and the suspended one sees what they did
    ///     as it goes on.
    /// </summary>
    [Fact]
    public void AnActorLetsOtherCallsInWhileOneWaits() =>
        Assert.Equal(
            "true\n",
            AssertAgree(AsyncProgram("""
                    actor Account {
                        var balance = 0
                        let changed = WaitList()

                        fn deposit(_ amount: Int) {
                            balance += amount
                            changed.wakeAll()
                        }

                        fn withdrawWhenThere(_ amount: Int) async -> Int {
                            while balance < amount {
                                await changed.park()
                            }
                            balance -= amount
                            return balance
                        }
                    }
                    """, """
                    let account = Account()
                    let waiting = Task.spawn { await account.withdrawWhenThere(50) }
                    for _ in 0..<10 {
                        await account.deposit(10)
                    }
                    let left = await waiting.value
                    print(left >= 0 && left + 50 == 100)
                """)).Stdout
        );

    /// <summary>Channels between many senders and receivers, each on its own task: every value arrives once.</summary>
    [Fact]
    public void ChannelsCarryEveryValueBetweenManyTasks() =>
        Assert.Equal(
            "319600\n4\n",
            AssertAgree(AsyncProgram("import Sable.Concurrency", """
                    let work = Channel<Int>()
                    let done = Channel<Int>()
                    var receivers: Task<Int>[>..] = []
                    for _ in 0..<4 {
                        receivers.append(Task.spawn {
                            var total = 0
                            for await value in work {
                                total += value
                            }
                            await done.send(total)
                            return 0
                        })
                    }
                    var senders: Task<Int>[>..] = []
                    for s in 0..<4 {
                        senders.append(Task.spawn {
                            for i in 0..<200 {
                                await work.send(s * 200 + i)
                            }
                            return 0
                        })
                    }
                    for sender in senders {
                        let _ = await sender.value
                    }
                    await work.close()
                    for receiver in receivers {
                        let _ = await receiver.value
                    }
                    var total = 0
                    var parts = 0
                    while let part = await done.tryReceive() {
                        total += part
                        parts += 1
                    }
                    print(total)
                    print(parts)
                """), stepLimit: 50_000_000).Stdout
        );

    /// <summary>
    ///     Objects passed between tasks — captured by their closures, sent
    ///     through a channel, given back as results — are counted atomically:
    ///     many tasks copying and dropping the same objects at once leave
    ///     every count right, and nothing leaked (each run is held to that).
    /// </summary>
    [Fact]
    public void ObjectsSharedBetweenTasksAreCountedRight() =>
        Assert.Equal(
            "6400\n64\n",
            AssertAgree(AsyncProgram("""
                    import Sable.Concurrency

                    @immutable
                    class Node {
                        let value: Int
                        let next: Node?
                        init(_ value: Int, _ next: Node?) {
                            self.value = value
                            self.next = next
                        }
                    }

                    fn sum(_ node: Node?) -> Int {
                        var total = 0
                        var at = node
                        while let n = at {
                            total += n.value
                            at = n.next
                        }
                        return total
                    }
                    """, """
                    var list: Node? = none
                    for i in 0..<100 {
                        list = Node(i % 2, list)
                    }
                    let shared = list
                    let results = Channel<Node>()
                    var tasks: Task<Int>[>..] = []
                    for t in 0..<64 {
                        tasks.append(Task.spawn {
                            var total = 0
                            for _ in 0..<2 {
                                let copy = shared
                                total += sum(copy)
                            }
                            await results.send(Node(t, shared))
                            return total
                        })
                    }
                    var total = 0
                    for task in tasks {
                        total += await task.value
                    }
                    var received = 0
                    while let node = await results.tryReceive() {
                        received += 1
                        let _ = sum(node)
                    }
                    print(total)
                    print(received)
                """)).Stdout
        );

    /// <summary>
    ///     The run queue is ordered by priority: queued together, the high
    ///     task runs first and the background one last. With no pool, so
    ///     that the order the tasks run in is the order they are taken; the
    ///     main task awaits them highest first, so that waiting escalates
    ///     none of them past another.
    /// </summary>
    [Fact]
    public void PrioritiesOrderTheRunQueue() =>
        Assert.Equal(
            "high\nmedium\nlow\nbackground\n",
            AssertAgree(AsyncProgram("", """
                    let b = Task.spawn(priority: .background) { print("background") }
                    let l = Task.spawn(priority: .low) { print("low") }
                    let m = Task.spawn { print("medium") }
                    let h = Task.spawn(priority: .high) { print("high") }
                    await h.value
                    await m.value
                    await l.value
                    await b.value
                """), environment: OneThread).Stdout
        );

    /// <summary>
    ///     A high-priority task waiting for a background one escalates it:
    ///     queued already, it moves up, ahead of a low-priority task queued
    ///     before it — but its own priority, as it reads it, is unchanged.
    /// </summary>
    [Fact]
    public void AWaitingTaskEscalatesTheTaskItAwaits() =>
        Assert.Equal(
            "background at 9\nhigh\nlow\n",
            AssertAgree(AsyncProgram("", """
                    let b = Task.spawn(priority: .background) { print("background at \(Task.currentPriority.rawValue)") }
                    let l = Task.spawn(priority: .low) { print("low") }
                    let h = Task.spawn(priority: .high) {
                        await b.value
                        print("high")
                    }
                    await h.value
                    await l.value
                """), environment: OneThread).Stdout
        );

    /// <summary>
    ///     Spawning and awaiting a hundred thousand tasks on the pool: every
    ///     result arrives, the pool's threads are as many as the cores, and
    ///     no task or object is left as the program ends.
    /// </summary>
    [Fact]
    public void ManyTasksOnThePoolLeaveNothing() {
        if (!CanBuild) {
            return;
        }

        var run = BuildAndRun(AsyncProgram("", """
                var tasks: Task<Int>[>..] = []
                for i in 0..<100_000 {
                    tasks.append(Task.spawn { i % 7 })
                }
                var total = 0
                for task in tasks {
                    total += await task.value
                }
                print(total)
            """), environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" });
        Assert.Equal("299995\n", run.Stdout);
        Assert.DoesNotContain("leaked", run.Stderr, StringComparison.Ordinal);
        Assert.Matches(@"tasks 100000, \d+ at most at once, 0 dropped", run.Stderr);
        Assert.Equal(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture), Regex.Match(run.Stderr, @"(\d+) workers").Groups[1].Value);
    }
}
