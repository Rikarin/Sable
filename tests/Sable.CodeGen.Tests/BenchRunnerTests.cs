using Sable.Driver.Commands;
using Sable.Symbols;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable bench</c> (docs/05 §5.1): the harness around the
///     <c>@bench</c> functions, its report and its baselines. Times are not
///     held to anything; the shape of what is printed is, and the arithmetic
///     of the statistics and the comparison, on fixed numbers, natively and
///     interpreted. The command prints to the console, which other tests
///     take over to read it: so not at the same time as those.
/// </summary>
[Collection("Console")]
public sealed class BenchRunnerTests {
    const string Benches = """
        module Speed

        import Sable.Testing

        fn work(_ n: Int) -> Int {
            var total = 0
            for i in 0..<n {
                total = total + i
            }
            return total
        }

        @bench
        fn summing(_ b: Bencher) {
            let n = 50
            b.iterate(() => {
                let total = work(n)
            })
        }

        @bench
        fn timedWhole() {
            let total = work(10)
        }

        struct Grouped {
            @bench
            static fn joining() {
                let text = "a" + "b"
            }
        }

        @bench
        fn forgets(_ b: Bencher) {
        }
        """;

    const string Line = @"mean [0-9.]+ (ns|µs|ms) / iter, median [0-9.]+ (ns|µs|ms), ± [0-9.]+ (ns|µs|ms) \(3 × \d+ iterations\)";

    [Fact]
    public void EachBenchmarkReportsItsTimePerIteration() {
        if (!CanBuild) {
            return;
        }

        var run = Native(Benches, TestKind.Bench, ["--quick"]);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.Stderr);
        Assert.Matches(
            $"^running 4 benchmarks\nbench Speed\\.summing \\.\\.\\. {Line}\nbench Speed\\.timedWhole \\.\\.\\. {Line}\nbench Speed\\.Grouped\\.joining \\.\\.\\. {Line}\nbench Speed\\.forgets \\.\\.\\. did not call 'iterate'\n\nbench result: 3 measured\n$",
            run.Stdout
        );
    }

    [Fact]
    public void FiltersAndListing() {
        if (!CanBuild) {
            return;
        }

        var list = Native(Benches, TestKind.Bench, ["--list", "ing"]);
        Assert.Equal("Speed.summing: bench (test.sb:14)\nSpeed.Grouped.joining: bench (test.sb:28)\n2 benchmarks\n", list.Stdout);

        var one = Native(Benches, TestKind.Bench, ["--quick", "Whole"]);
        Assert.Matches($"^running 1 benchmark\nbench Speed\\.timedWhole \\.\\.\\. {Line}\n\nbench result: 1 measured\n$", one.Stdout);
    }

    [Fact]
    public void ABaselineIsSavedAndComparedWith() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-bench-");
        try {
            var place = Path.Combine(directory.FullName, "nested/bench");
            var saved = Native(Benches, TestKind.Bench, ["--quick", "--save-baseline", "before", "--baseline-dir", place, "summing", "joining"]);
            Assert.Equal(0, saved.ExitCode);
            Assert.EndsWith($"saved baseline 'before' to {place}/before.txt\n", saved.Stdout, StringComparison.Ordinal);

            var lines = File.ReadAllLines(Path.Combine(place, "before.txt"));
            Assert.StartsWith("# sable bench baseline", lines[0], StringComparison.Ordinal);
            Assert.Equal(3, lines.Length);
            Assert.Matches(@"^Speed\.summing\t\d+\t\d+\t\d+\t\d+$", lines[1]);
            Assert.Matches(@"^Speed\.Grouped\.joining\t\d+\t\d+\t\d+\t\d+$", lines[2]);

            var compared = Native(Benches, TestKind.Bench, ["--quick", "--baseline", "before", "--baseline-dir", place, "summing", "Whole"]);
            Assert.Equal(0, compared.ExitCode);
            Assert.StartsWith("running 2 benchmarks against baseline 'before'\n", compared.Stdout, StringComparison.Ordinal);
            Assert.Matches($"bench Speed\\.summing \\.\\.\\. {Line}; [+-][0-9.]+% vs baseline \\((within noise|improved|REGRESSED)\\)\n", compared.Stdout);
            Assert.Matches($"bench Speed\\.timedWhole \\.\\.\\. {Line}; new\n", compared.Stdout);
            Assert.Matches(@"\nbench result: 2 measured; [01] regressed\n$", compared.Stdout);

            var missing = Native(Benches, TestKind.Bench, ["--quick", "--baseline", "after", "--baseline-dir", place]);
            Assert.Equal(2, missing.ExitCode);
            Assert.Equal($"no baseline 'after' at {place}/after.txt\n", missing.Stderr);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The statistics, the baseline's format and the verdicts, on numbers chosen rather than measured.</summary>
    [Fact]
    public void TheArithmeticOnFixedNumbers() =>
        Assert.Equal(
            """
            mean 5.00 median 4.50 deviation 2.94
            mean 4.00 median 4.00 deviation 0.00
            +10.00% vs baseline (REGRESSED)
            +3.00% vs baseline (within noise)
            -20.00% vs baseline (improved)
            +10.00% vs baseline (within noise)
            Speed.a 5000 4500 2943 100
            Speed.b 1500 1500 0 7
            2 Speed.b 1.50
            850 ns|12.40 µs|3.05 ms|1.20 s|7.25 ns

            """,
            Programs.AssertAgree("""
                module Fixed
                import Sable.Testing
                import Sable.Text

                fn show(_ r: BenchResult) {
                    print("mean \(formatted(r.mean, decimals: 2)) median \(formatted(r.median, decimals: 2)) deviation \(formatted(r.deviation, decimals: 2))")
                }

                fn result(_ mean: F64, _ deviation: F64) -> BenchResult {
                    return BenchResult { name: "x", mean: mean, median: mean, deviation: deviation, iterations: 1 }
                }

                @main
                fn main() {
                    let a = summarise("Speed.a", [2.0, 4.0, 5.0, 9.0], 100)
                    show(a)
                    show(summarise("one", [4.0], 1))

                    print(comparison(result(100.0, 1.0), result(110.0, 1.0)).0)
                    print(comparison(result(100.0, 1.0), result(103.0, 1.0)).0)
                    print(comparison(result(100.0, 1.0), result(80.0, 1.0)).0)
                    // Noisy runs: 10% is within twice their deviations.
                    print(comparison(result(100.0, 3.0), result(110.0, 3.0)).0)

                    let b = BenchResult { name: "Speed.b", mean: 1.5, median: 1.5, deviation: 0.0, iterations: 7 }
                    let text = formatBaseline([a, b])
                    for line in lines(text) {
                        if !hasPrefix(line, "#") {
                            print(replacing(line, "\t", with: " "))
                        }
                    }
                    let back = parseBaseline(text)
                    print("\(back.count) \(back[1].name) \(formatted(back[1].mean, decimals: 2))")

                    print("\(formatDuration(850))|\(formatDuration(12400))|\(formatDuration(3050000))|\(formatDuration(1200000000))|\(formatNanoseconds(7.25))")
                }
                """).Stdout
        );

    [Fact]
    public void TheBenchCommandRunsThem() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-bench-command-");
        try {
            var file = Path.Combine(directory.FullName, "speed.sb");
            File.WriteAllText(file, Benches);
            var bench = Command.Find("bench")!;
            Assert.Equal(0, bench.Run([file, "--quick", "--baseline-dir", Path.Combine(directory.FullName, "b"), "--save-baseline", "x", "summing"]));
            Assert.True(File.Exists(Path.Combine(directory.FullName, "b/x.txt")));
            Assert.Equal(0, bench.Run([file, "--list"]));
            Assert.Equal(2, bench.Run([file, "--doc"]));
            Assert.Equal(2, bench.Run([file, "--save-baseline"]));
        } finally {
            directory.Delete(true);
        }
    }
}
