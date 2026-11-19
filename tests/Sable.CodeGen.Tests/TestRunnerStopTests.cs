using System.Diagnostics;
using Sable.Driver;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The runner of <c>sable test</c> stops a test past its time limit, and
///     never waits without a bound for one: a stuck test was once left
///     spinning, and the whole run with it, waiting for a process the kill
///     had missed.
/// </summary>
public sealed class TestRunnerStopTests {
    const string Spins = """
        module Spin

        import Sable.Testing

        @test
        fn spins() {
            var n = 0
            while true {
                n = n + 1
                if n < 0 {
                    print(n)
                }
            }
        }
        """;

    [Fact]
    public void ACpuBoundTestPastItsLimitIsAlwaysStopped() {
        if (!CanBuild) {
            return;
        }

        // A name of its own, to look for afterwards among every process.
        var name = $"spin{Guid.NewGuid():N}"[..12];
        var harness = Platform.Executable(Path.Combine(Path.GetTempPath(), name));
        try {
            var compiled = Compile(Spins);
            Assert.True(compiled.Succeeded, string.Join("\n", compiled.Errors));
            var build = NativeBuild.Build(compiled.LlvmIr!, harness, false);
            Assert.True(build.Succeeded, build.Output);

            // Many at once, each stopped at its limit, and stopped for good.
            var results = new TestRunner.Captured[16];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                i => results[i] = TestRunner.Capture(harness, ["--run-test", "Spin.spins"], TimeSpan.FromMilliseconds(500), null));
            Assert.All(results, static r => Assert.True(r.TimedOut && !r.Stuck, r.ToString()));
            Assert.Empty(Process.GetProcessesByName(name));
        } finally {
            Platform.DeleteExecutable(harness);
        }
    }

    [Fact]
    public void AProcessPastItsLimitIsStoppedWithWhatItStarted() {
        if (OperatingSystem.IsWindows()) {
            return;
        }

        // The shell starts a loop of its own, which holds the output too.
        var clock = Stopwatch.StartNew();
        var result = TestRunner.Capture("/bin/sh", ["-c", "echo started; (while :; do :; done) & while :; do :; done"], TimeSpan.FromMilliseconds(500), null);
        Assert.True(result.TimedOut && !result.Stuck, result.ToString());
        Assert.Equal("started\n", result.Output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }

    [Fact]
    public void AProcessThatEndsButLeavesItsOutputOpenIsNotWaitedForForever() {
        if (OperatingSystem.IsWindows()) {
            return;
        }

        // Ended at once; what it left behind keeps the output open a while longer than the runner waits.
        var clock = Stopwatch.StartNew();
        var result = TestRunner.Capture("/bin/sh", ["-c", "echo ended; sleep 40 &"], null, null);
        Assert.False(result.TimedOut);
        Assert.True(result.Stuck, result.ToString());
        Assert.Equal(0, result.Status);
        Assert.Equal("ended\n", result.Output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }
}
