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
            b.itemsPerIteration = n
            b.check("\(b.iterate { work(n) })")
        }

        @bench
        fn timedWhole() {
            _ = blackBox(work(10))
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

    const string Line = @"[0-9.]+ (ns|µs|ms) ± [0-9.]+ (ns|µs|ms) \(median [0-9.]+ (ns|µs|ms), [23] × \d+ iterations(, 1 outliers?)?\), allocations [0-9.]+ \([0-9.]+ [KM]?i?B\), retains [0-9.]+, releases [0-9.]+(, [0-9.]+ [kMGT]?(B|items)/s)*";

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

    /// <summary>
    ///     An async benchmark is awaited: given the Bencher, it awaits
    ///     <c>iterateAsync</c>; without, it is timed whole, each iteration
    ///     awaited. What one computes is kept with <c>blackBox</c>.
    /// </summary>
    [Fact]
    public void AnAsyncBenchmarkIsAwaited() {
        if (!CanBuild) {
            return;
        }

        var run = Native("""
            module Waiting

            import Sable.Testing

            async fn twice(_ n: Int) -> Int {
                await Task.yield()
                return n * 2
            }

            @bench
            async fn awaited(_ b: Bencher) {
                await b.iterateAsync(() => {
                    let doubled = blackBox(await twice(blackBox(21)))
                })
            }

            @bench
            async fn timedWhole() {
                let doubled = blackBox(await twice(4))
            }
            """, TestKind.Bench, ["--quick"]);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("", run.Stderr);
        Assert.Matches($"^running 2 benchmarks\nbench Waiting\\.awaited \\.\\.\\. {Line}\nbench Waiting\\.timedWhole \\.\\.\\. {Line}\n\nbench result: 2 measured\n$", run.Stdout);
    }

    [Fact]
    public void FiltersAndListing() {
        if (!CanBuild) {
            return;
        }

        var list = Native(Benches, TestKind.Bench, ["--list", "ing"]);
        Assert.Equal("Speed.summing: bench (test.sb:14)\nSpeed.Grouped.joining: bench (test.sb:27)\n2 benchmarks\n", list.Stdout);

        var described = Native(Benches, TestKind.Bench, ["--describe", "--filter", "name:*.timed* | forgets"]);
        Assert.Matches(@"^Speed\.timedWhole\ttest\.sb:\d+\t-\t0\t\nSpeed\.forgets\ttest\.sb:\d+\t-\t0\t\nfiltered out\t2\n$", described.Stdout);
        Assert.Equal(2, Native(Benches, TestKind.Bench, ["--filter", "("]).ExitCode);
        Assert.Equal(2, Native(Benches, TestKind.Bench, ["--samples"]).ExitCode);
        Assert.Equal(2, Native(Benches, TestKind.Bench, ["--samples", "none"]).ExitCode);
        Assert.Equal(3, Native(Benches, TestKind.Bench, ["--run-bench", "Speed.none"]).ExitCode);

        var one = Native(Benches, TestKind.Bench, ["--quick", "Whole"]);
        Assert.Matches($"^running 1 benchmark\nbench Speed\\.timedWhole \\.\\.\\. {Line}\n\nbench result: 1 measured\n$", one.Stdout);
    }

    /// <summary>'arguments:' makes a benchmark for each value, named with it; 'tags:' is what a filter asks for.</summary>
    [Fact]
    public void ABenchmarkWithArgumentsIsOneForEach() {
        if (!CanBuild) {
            return;
        }

        const string program = """
            module Sizes

            import Sable.Testing

            @bench(arguments: [16, 64], tags: ["copy"])
            fn filled(_ b: Bencher, _ size: Int) {
                b.bytesPerIteration = size
                b.check("\(b.iterate { U8[>..](repeating: 1, count: size) }.count)")
            }

            struct Pairs {
                @bench(arguments: [("a", 1), ("b", 2)])
                static fn repeated(text: String, count: Int) {
                    _ = blackBox(text.repeated(count))
                }
            }

            @bench(arguments: [1], [2, 3])
            async fn products(b: Bencher, x: Int, y: Int) {
                b.check("\(await b.iterateAsync { x * y })")
            }
            """;
        var run = Native(program, TestKind.Bench, ["--quick"]);
        Assert.Equal("", run.Stderr);
        Assert.Equal(0, run.ExitCode);
        Assert.Matches($"^running 6 benchmarks\nbench Sizes\\.filled\\(16\\) \\.\\.\\. {Line}\nbench Sizes\\.filled\\(64\\) \\.\\.\\. {Line}\n"
            + $"bench Sizes\\.Pairs\\.repeated\\(\"a\", 1\\) \\.\\.\\. {Line}\nbench Sizes\\.Pairs\\.repeated\\(\"b\", 2\\) \\.\\.\\. {Line}\n"
            + $"bench Sizes\\.products\\(1, 2\\) \\.\\.\\. {Line}\nbench Sizes\\.products\\(1, 3\\) \\.\\.\\. {Line}\n\nbench result: 6 measured\n$", run.Stdout);
        Assert.Equal("Sizes.filled(16)\ttest.sb:6\t-\t0\tcopy\nSizes.filled(64)\ttest.sb:6\t-\t0\tcopy\nfiltered out\t4\n", Native(program, TestKind.Bench, ["--describe", "--filter", "tag:copy"]).Stdout);
        Assert.Equal("Sizes.filled(64): bench (test.sb:6)\n1 benchmark\n", Native(program, TestKind.Bench, ["--list", "--exact", "Sizes.filled(64)"]).Stdout);
    }

    /// <summary>A trap in a benchmark fails it, and the run, and no other benchmark.</summary>
    [Fact]
    public void ATrapFailsItsBenchmarkAlone() {
        if (!CanBuild) {
            return;
        }

        var run = Native("""
            module Failing

            import Sable.Testing

            @bench
            fn traps(_ b: Bencher) {
                b.iterate { fatalError("no good") }
            }

            @bench
            fn works() {
            }
            """, TestKind.Bench, ["--quick"]);
        Assert.Equal(1, run.ExitCode);
        Assert.Matches($"bench Failing\\.traps \\.\\.\\. FAILED \\(exit status 134\\)\n    fatal error: no good\n(    .*\n)*bench Failing\\.works \\.\\.\\. {Line}\n\nbench result: 1 measured; 1 failed\n$", run.Stdout);
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
            Assert.Matches(@"^Speed\.summing(\t[0-9.]+ [0-9.]+){3}$", lines[1]);
            Assert.Matches(@"^Speed\.Grouped\.joining(\t[0-9.]+ [0-9.]+){3}$", lines[2]);

            var compared = Native(Benches, TestKind.Bench, ["--quick", "--baseline", "before", "--baseline-dir", place, "summing", "Whole"]);
            Assert.Equal(0, compared.ExitCode);
            Assert.StartsWith("running 2 benchmarks against baseline 'before'\n", compared.Stdout, StringComparison.Ordinal);
            Assert.Matches($"bench Speed\\.summing \\.\\.\\. {Line}; [+-][0-9.]+% vs baseline \\(within noise, p = [0-9.]+\\)\n", compared.Stdout);
            Assert.Matches($"bench Speed\\.timedWhole \\.\\.\\. {Line}; new\n", compared.Stdout);
            Assert.Matches(@"\nbench result: 2 measured; 0 regressed\n$", compared.Stdout);

            // Slower than a baseline that says it took no time to speak of, for certain.
            File.WriteAllText(Path.Combine(place, "fast.txt"), $"Speed.summing{string.Concat(Enumerable.Range(1, 20).Select(static i => $"\t0.0{i} 0.0{i}"))}\n");
            string[] slow = ["--samples", "20", "--sample-ms", "0.1", "--warm-up-ms", "0.1", "--baseline", "fast", "--baseline-dir", place, "summing"];
            var slower = Native(Benches, TestKind.Bench, slow);
            Assert.EndsWith("bench result: 1 measured; 1 regressed\n", slower.Stdout, StringComparison.Ordinal);
            Assert.Equal(0, slower.ExitCode);
            Assert.Equal(1, Native(Benches, TestKind.Bench, [..slow, "--fail-on-regression"]).ExitCode);

            var missing = Native(Benches, TestKind.Bench, ["--quick", "--baseline", "after", "--baseline-dir", place]);
            Assert.Equal(2, missing.ExitCode);
            Assert.Equal($"no baseline 'after' at {place}/after.txt\n", missing.Stderr);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The statistics and the verdicts, on numbers chosen rather than measured (tests/lib/Sable.Testing has the rest).</summary>
    [Fact]
    public void TheArithmeticOnFixedNumbers() =>
        Assert.Equal(
            """
            5.00 4.50 2.94
            6 1 10.50
            BenchVerdict.Regressed BenchVerdict.Improved BenchVerdict.WithinNoise
            0.007937
            4.1405 12.7062
            850 ns|12.40 µs|3.05 ms|1.20 s|7.25 ns

            """,
            Programs.AssertAgree("""
                module Fixed
                import Sable.Testing

                @main
                fn main() {
                    let a = BenchStatistics([2.0, 4.0, 5.0, 9.0], outliers: .Keep)
                    print("\(a.mean:.2f) \(a.median:.2f) \(a.deviation:.2f)")
                    let b = BenchStatistics([10.0, 11.0, 10.0, 11.0, 10.0, 11.0, 50.0])
                    print("\(b.count) \(b.removed) \(b.mean:.2f)")

                    var before: F64[>..] = []
                    var after: F64[>..] = []
                    for i in 0..<12 {
                        before.append(100.0 + F64(i) / 10.0)
                        after.append(120.0 + F64(i) / 10.0)
                    }
                    print("\(BenchComparison(before, after).verdict:?) \(BenchComparison(after, before).verdict:?) \(BenchComparison(before, before).verdict:?)")
                    print("\(MannWhitney([1.0, 2.0, 3.0, 4.0, 5.0], [6.0, 7.0, 8.0, 9.0, 10.0]).probability:.6f)")
                    print("\(studentQuantile(0.9995, 14):.4f) \(studentQuantile(0.975, 1):.4f)")
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
            Assert.Equal(2, bench.Run([file, "--job", "no words"]));
            Assert.Equal(2, bench.Run([file, "--job", "a=not-a-word"]));
            Assert.Equal(2, bench.Run([file, "--job", "a=-O0", "--job", "a=-O1"]));

            // Each format, and a history's rows, which are added to.
            string At(string name) => Path.Combine(directory.FullName, name);
            for (var i = 0; i < 2; i++) {
                Assert.Equal(0, bench.Run([file, "--quick", "--markdown", At("m.md"), "--csv", At("c.csv"), "--json", At("j.json"), "--history", At("h.tsv"), "summing"]));
            }

            Assert.Matches(@"^\| benchmark \|.*\n\|:--\|.*\n\| `Speed\.summing` \|.*\n$", File.ReadAllText(At("m.md")));
            Assert.Matches(@"^benchmark,job,mean_ns,.*\nSpeed\.summing,,[0-9.]+,.*\n$", File.ReadAllText(At("c.csv")));
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(At("j.json")));
            var result = json.RootElement.GetProperty("benchmarks")[0];
            Assert.Equal("1225", result.GetProperty("check").GetString());
            Assert.Equal(3, result.GetProperty("processor").GetArrayLength());
            Assert.Matches(@"^date\tcommit\tbenchmark\tms\tallocations\tretains\treleases\n(\d{4}-\d\d-\d\d\t\S+\tSpeed\.summing\t[0-9.]+\t0\t0\t0\n){2}$", File.ReadAllText(At("h.tsv")));
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>What the benchmarks are compiled to, in place of a run; each a function of its own, and its closures.</summary>
    [Fact]
    public void TheSilAndTheAssemblyOfABenchmark() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-bench-asm-");
        try {
            File.WriteAllText(Path.Combine(directory.FullName, "speed.sb"), Benches);
            var (code, output, errors) = Sable(directory.FullName, "bench", "speed.sb", "Whole", "--sil", "--asm");
            Assert.True(code == 0, errors);
            Assert.StartsWith("sil @Speed.timedWhole : $() -> ()", output, StringComparison.Ordinal);
            Assert.Contains("sable.Speed.timedWhole", output[output.IndexOf("\n}\n", StringComparison.Ordinal)..], StringComparison.Ordinal);
            Assert.DoesNotContain("joining", output, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The benchmarks as they are at a commit, and as they are now, run in turn and compared; what one computed differs, and fails.</summary>
    [Fact]
    public void ComparedWithACommit() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-bench-git-");
        try {
            void Git(params string[] arguments) => Assert.Equal(0, Start("git", directory.FullName, ["-c", "user.name=t", "-c", "user.email=t@example.org", ..arguments]).ExitCode);
            var place = Directory.CreateDirectory(Path.Combine(directory.FullName, "inner")).FullName;
            var file = Path.Combine(place, "speed.sb");
            File.WriteAllText(file, Benches);
            Git("init", "-q");
            Git("add", ".");
            Git("commit", "-q", "-m", "first");
            File.WriteAllText(file, Benches.Replace("let n = 50", "let n = 60", StringComparison.Ordinal).Replace("fn timedWhole", "fn timedNew", StringComparison.Ordinal));

            var (code, output, errors) = Sable(place, "bench", "speed.sb", "--quick", "--launches", "1", "--compare", "HEAD", "summing", "timed");
            Assert.True(code == 1, errors + output);
            Assert.Contains("bench Speed.summing ... computed '1225', and then '1770'\n", output, StringComparison.Ordinal);
            Assert.Matches($"bench Speed\\.summing \\[HEAD\\] \\.\\.\\. {Line}\n", output);
            Assert.Contains("bench Speed.summing [this] ... ", output, StringComparison.Ordinal);
            Assert.Contains("% vs HEAD (", output, StringComparison.Ordinal);
            Assert.Contains("bench Speed.timedNew [HEAD] ... not there\n", output, StringComparison.Ordinal);
            Assert.Contains("bench Speed.timedNew [this] ... ", output, StringComparison.Ordinal);

            (code, _, errors) = Sable(place, "bench", "speed.sb", "--compare", "no-such-commit");
            Assert.Equal(2, code);
            Assert.Contains("'no-such-commit' is neither a file nor a commit", errors, StringComparison.Ordinal);
        } finally {
            // Git's objects are read-only, which Windows does not delete.
            foreach (var path in Directory.GetFiles(directory.FullName, "*", SearchOption.AllDirectories)) {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            directory.Delete(true);
        }
    }

    static (int ExitCode, string Output, string Errors) Sable(string directory, params string[] arguments) =>
        Start(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet", directory, [typeof(Command).Assembly.Location, ..arguments]);

    static (int ExitCode, string Output, string Errors) Start(string program, string directory, string[] arguments) {
        var start = new System.Diagnostics.ProcessStartInfo(program, arguments) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Replace("\r\n", "\n", StringComparison.Ordinal), errors.Result);
    }
}
