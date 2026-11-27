using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Where threads run (runtime/topology.c): the machine's topology, a
///     thread's priority and affinity, and the pool's placement. The
///     interpreter and the native code agree where a program does not ask
///     what the system did; a pool placed before it starts has the size and
///     the priority asked for, which only a native program has.
/// </summary>
[Native]
public sealed class PlacementTests {
    // SABLE_WORKERS empty: the pool's size is the runtime's to choose.
    static readonly Dictionary<string, string> Unset = new() { ["SABLE_WORKERS"] = "" };

    [Fact]
    public void TheInterpreterAndTheNativeCodeAgree() {
        var run = AssertAgree("""
            module P
            import Sable.Concurrency

            @main
            fn main() {
                let topology = ProcessorTopology.current
                print(topology.processors.count >= 1)
                print(topology.cores.count <= topology.processors.count)
                print(topology.nodes.count >= 1 && !topology.performanceProcessors.isEmpty)
                print(Thread.spawn(priority: .Low) { Thread.priority }.join())
                print(Thread.spawn { Thread.setPriority(.Low) && Thread.priority == .Low }.join())
                print(Thread.setAffinity([]))
                print((Thread.affinity ?? [0]).count >= 1)
            }
            """);
        Assert.Equal("true\ntrue\ntrue\nLow\ntrue\nfalse\ntrue\n", run.Stdout);
    }

    [Fact]
    public void ReservedCoresAreLeftOutOfThePool() {
        if (!CanBuild || !NativeMode.Wanted) {
            return;
        }

        var run = BuildAndRun("""
            module P
            import Sable.Concurrency

            @main
            fn main() {
                let wanted = Thread.availableParallelism - 1
                print(WorkerPool.configure(reserving: 1))
                print(WorkerPool.count == (wanted > 0 ? wanted : 1))
                // Started: its size is as it was.
                print(WorkerPool.configure(reserving: 2))
                print(WorkerPool.configure(reserving: 1))
            }
            """, environment: Unset);
        Assert.Equal("true\ntrue\nfalse\ntrue\n", run.Stdout);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void ThePoolIsAsLargeAsTheProcessorsItIsGiven() {
        if (!CanBuild || !NativeMode.Wanted) {
            return;
        }

        var run = BuildAndRun("""
            module P
            import Sable.Concurrency

            @main
            fn main() async {
                let one = (Thread.affinity ?? ProcessorTopology.current.ids)[0]
                print(WorkerPool.configure(priority: .Low, processors: [one]))
                print(WorkerPool.count)
                // Each worker is placed before it runs a task.
                print(await Task.spawn { Thread.priority }.value)
                print(await Task.spawn { (Thread.affinity ?? [one]) == [one] }.value)
            }
            """, environment: Unset);
        Assert.Equal("true\n1\nLow\ntrue\n", run.Stdout);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void SableWorkersSetsThePoolsSizeWhateverIsReserved() {
        if (!CanBuild || !NativeMode.Wanted) {
            return;
        }

        var run = BuildAndRun("""
            module P
            import Sable.Concurrency

            @main
            fn main() {
                print(WorkerPool.configure(reserving: 1))
                print(WorkerPool.count)
            }
            """, environment: new Dictionary<string, string> { ["SABLE_WORKERS"] = "2" });
        Assert.Equal("false\n2\n", run.Stdout);
        Assert.Equal(0, run.ExitCode);
    }
}
