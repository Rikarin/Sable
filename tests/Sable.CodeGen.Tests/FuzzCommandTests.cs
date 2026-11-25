using Sable.Driver.Commands;
using Sable.Symbols;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable fuzz</c> (docs/05 §5.1), without fuzzing: what its command
///     line means, what libFuzzer and clang are asked for, where inputs are
///     kept, the harness the compiler writes, a saved failing input replayed
///     under the sanitizers, and what is said where there is no libFuzzer. The
///     fuzzing itself is the nightly workflow's (.github/workflows/fuzz.yml).
/// </summary>
[Collection("Console")]
public sealed class FuzzCommandTests {
    const string Targets = """
        module Bytes

        union Bad: Error { Odd }

        @fuzz
        fn findsFuzz(_ data: U8[:]) {
            if data.count > 2 && data[0] == 70 && data[1] == 85 && data[2] == 90 {
                fatalError("found it")
            }
        }

        @fuzz
        fn throwsOnOne(_ data: U8[:]) throws(Bad) {
            if data.count == 1 {
                throw .Odd
            }
        }

        @main
        fn main() {
        }
        """;

    static FuzzOptions Parse(params string[] arguments) {
        var options = FuzzCommand.ParseArguments(arguments, out var error);
        Assert.True(options is not null, error);
        return options!;
    }

    static string Refused(params string[] arguments) {
        Assert.Null(FuzzCommand.ParseArguments(arguments, out var error));
        return error!;
    }

    [Fact]
    public void TheCommandLine() {
        var fuzz = Parse("parses", "--duration", "30m", "--", "-max_len=64", "-dict=x.dict");
        Assert.Equal("parses", fuzz.Target);
        Assert.Equal(TimeSpan.FromMinutes(30), fuzz.Duration);
        Assert.Equal(["-max_len=64", "-dict=x.dict"], fuzz.EngineArguments);
        Assert.False(fuzz.Replay || fuzz.List);

        var files = Parse("a.sb", "b.sb", "M.parses");
        Assert.Equal(["a.sb", "b.sb"], files.Files);
        Assert.Equal("M.parses", files.Target);
        Assert.Null(files.Duration);

        var replay = Parse("--replay", "M.parses", "crash-1", "crash-2");
        Assert.True(replay.Replay);
        Assert.Equal("M.parses", replay.Target);
        Assert.Equal(["crash-1", "crash-2"], replay.Inputs);

        Assert.Equal("crash-1", Parse("M.parses", "--minimise", "crash-1").Minimise);
        Assert.True(Parse("--list").List);

        var bounded = Parse("M.parses", "--runs", "5000", "--seeds", "a", "--seeds", "b", "--dict", "sable");
        Assert.Equal(5000, bounded.Runs);
        Assert.Equal(["a", "b"], bounded.Seeds);
        Assert.Equal("sable", bounded.Dictionary);
        Assert.Null(Parse().Target);
    }

    [Fact]
    public void WhatTheCommandLineRefuses() {
        Assert.Contains("unknown option '--time'", Refused("--time", "8h"), StringComparison.Ordinal);
        Assert.Contains("not 'soon'", Refused("--duration", "soon"), StringComparison.Ordinal);
        Assert.Contains("needs a value", Refused("--duration"), StringComparison.Ordinal);
        Assert.Contains("one target at a time", Refused("a", "b"), StringComparison.Ordinal);
        Assert.Contains("a command of their own", Refused("--list", "--replay"), StringComparison.Ordinal);
        Assert.Contains("fuzzing alone", Refused("--replay", "--duration", "1m"), StringComparison.Ordinal);
        Assert.Contains("fuzzing alone", Refused("--list", "--dict", "sable"), StringComparison.Ordinal);
        Assert.Contains("a number of inputs", Refused("--runs", "-3"), StringComparison.Ordinal);
    }

    [Fact]
    public void Durations() {
        Assert.Equal(TimeSpan.FromSeconds(90), FuzzCommand.ParseDuration("90"));
        Assert.Equal(TimeSpan.FromSeconds(90), FuzzCommand.ParseDuration("90s"));
        Assert.Equal(TimeSpan.FromMinutes(30), FuzzCommand.ParseDuration("30m"));
        Assert.Equal(TimeSpan.FromHours(8), FuzzCommand.ParseDuration("8h"));
        Assert.Equal(TimeSpan.FromMilliseconds(500), FuzzCommand.ParseDuration("500ms"));
        Assert.Null(FuzzCommand.ParseDuration("0s"));
        Assert.Null(FuzzCommand.ParseDuration("1.5h"));
        Assert.Null(FuzzCommand.ParseDuration("-1m"));
        Assert.Null(FuzzCommand.ParseDuration("8d"));
    }

    /// <summary>Each target's inputs under target/fuzz/&lt;Module.function&gt;/, and one build for all of them.</summary>
    [Fact]
    public void WhereInputsAreKept() {
        var target = Path.Combine("pkg", "target");
        var layout = FuzzLayout.For(target, "M.parses");
        Assert.Equal(Path.Combine(target, "fuzz", "M.parses", "corpus"), layout.Corpus);
        Assert.Equal(Path.Combine(target, "fuzz", "M.parses", "crashes"), layout.Crashes);
        Assert.Equal(Path.Combine(target, "fuzz", "M.parses", "summary.txt"), layout.Summary);
        Assert.Equal(Path.Combine(target, "fuzz", "build", "fuzzer"), layout.Fuzzer);
        Assert.Equal(Path.Combine(target, "fuzz", "build", "replay"), layout.ReplayProgram);

        // What is replayed: the failures, not their minimised copies or notes, then the corpus.
        var root = Directory.CreateTempSubdirectory("sable-fuzz-layout-").FullName;
        try {
            var kept = FuzzLayout.For(root, "M.parses");
            Directory.CreateDirectory(kept.Crashes);
            Directory.CreateDirectory(kept.Corpus);
            foreach (var name in new[] { "crash-b", "crash-a", "crash-a.min", "crash-a.txt", "timeout-c" }) {
                File.WriteAllText(Path.Combine(kept.Crashes, name), "x");
            }

            File.WriteAllText(Path.Combine(kept.Corpus, "0123"), "y");
            Assert.Equal(["crash-a", "crash-b", "timeout-c"], kept.SavedCrashes().Select(Path.GetFileName));
            Assert.Equal(["0123"], kept.SavedCorpus().Select(Path.GetFileName));
            Assert.Empty(FuzzLayout.For(root, "M.other").SavedCrashes());
        } finally {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     libFuzzer writes what it keeps to the first directory it is given,
    ///     the corpus; the package's seeds come after; failures go to crashes/;
    ///     fork mode keeps going past one; and what the user passed comes last.
    /// </summary>
    [Fact]
    public void WhatLibFuzzerIsAskedFor() {
        var layout = FuzzLayout.For("t", "M.f");
        var arguments = FuzzCommand.EngineArguments(layout, ["seeds"], TimeSpan.FromMinutes(90), 4, ["-max_len=64"]);
        Assert.Equal(layout.Corpus, arguments[0]);
        Assert.Equal("seeds", arguments[1]);
        Assert.Contains($"-artifact_prefix={layout.Crashes}{Path.DirectorySeparatorChar}", arguments);
        Assert.Contains("-fork=4", arguments);
        Assert.Contains("-ignore_crashes=1", arguments);
        Assert.Contains("-ignore_timeouts=1", arguments);
        Assert.Contains("-max_total_time=5400", arguments);
        Assert.Contains("-timeout=20", arguments);
        Assert.Equal("-max_len=64", arguments[^1]);

        // Until stopped, with the user's own time limit for an input.
        var open = FuzzCommand.EngineArguments(layout, [], null, 1, ["-timeout=3"]);
        Assert.DoesNotContain(open, static a => a.StartsWith("-max_total_time", StringComparison.Ordinal));
        Assert.Equal(["-timeout=3"], open.Where(static a => a.StartsWith("-timeout=", StringComparison.Ordinal)));
        Assert.Contains("-fork=1", open);

        // So many runs: one process, which stops at the first failure.
        var runs = FuzzCommand.EngineArguments(layout, [], null, 4, [], 5000, "t.dict");
        Assert.Contains("-runs=5000", runs);
        Assert.Contains("-dict=t.dict", runs);
        Assert.DoesNotContain(runs, static a => a.StartsWith("-fork", StringComparison.Ordinal) || a.StartsWith("-ignore", StringComparison.Ordinal));

        Assert.Equal(["-minimize_crash=1", "-max_total_time=60", "-timeout=20", "-exact_artifact_path=c.min", "c"], FuzzCommand.MinimiseArguments("c", 60));
    }

    /// <summary>The fuzzer has libFuzzer and both sanitizers; the replay build, the sanitizers and a main of its own.</summary>
    [Fact]
    public void WhatClangIsAskedFor() {
        var fuzzer = FuzzBuild.Flags(FuzzEngine.LibFuzzer);
        Assert.Contains("-fsanitize=fuzzer,address,undefined", fuzzer);
        Assert.Contains("-fno-sanitize-recover=all", fuzzer);
        Assert.Contains("-fno-omit-frame-pointer", fuzzer);
        Assert.DoesNotContain("-DSABLE_FUZZ_REPLAY", fuzzer);

        var replay = FuzzBuild.Flags(FuzzEngine.Replay);
        Assert.Contains("-fsanitize=address,undefined", replay);
        Assert.Contains("-DSABLE_FUZZ_REPLAY", replay);
        Assert.Contains("-DSABLE_FUZZ_MUTATOR", FuzzBuild.Flags(FuzzEngine.LibFuzzer, mutates: true));
        Assert.DoesNotContain("-DSABLE_FUZZ_MUTATOR", fuzzer);

        // The program's own code is checked by ASan too: every function asks for it.
        var ir = NativeBuild.ForTarget("define i64 @\"M.f\"(i64 %a) nounwind {\n}\n", NativeTarget.Host, extraAttributes: FuzzBuild.ProgramAttributes);
        Assert.Contains("define i64 @\"M.f\"(i64 %a) #9000 nounwind {", ir, StringComparison.Ordinal);
        Assert.Matches("attributes #9000 = \\{ .*sanitize_address optforfuzzing \\}", ir);
        Assert.DoesNotContain(replay, static f => f.Contains("fuzzer", StringComparison.Ordinal));

        var host = NativeTarget.Host;
        var arguments = FuzzBuild.Arguments("h.ll", "rt.c", "fuzz.c", "out", FuzzEngine.LibFuzzer, host, ["-lz"]);
        var output = arguments.ToList().IndexOf("-o");
        Assert.Equal(["h.ll", "rt.c", "fuzz.c", "-o", "out"], arguments.Skip(output - 3).Take(5));
        Assert.Equal("-lz", arguments[^1]);
        Assert.Contains("LLVMFuzzerTestOneInput", FuzzBuild.Shim, StringComparison.Ordinal);
        Assert.Contains("getenv(\"SABLE_FUZZ_TARGET\")", FuzzBuild.Shim, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A fuzz build is a library: no entry point, the program's own
    ///     <c>@main</c> left alone, and what C calls exported.
    /// </summary>
    [Fact]
    public void TheHarnessIsALibraryCCalls() {
        var result = Compile(Targets, TestKind.Fuzz);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.True(result.Sil!.IsLibrary);
        Assert.Null(result.Sil.EntryPoint);
        Assert.Equal(["sable_harness_fuzz_begin", "sable_harness_fuzz_push", "sable_harness_fuzz_run"], result.Sil.Exports.Values.Order(StringComparer.Ordinal));
        Assert.Contains("@\"sable_harness_fuzz_run\"", result.LlvmIr!, StringComparison.Ordinal);
        Assert.DoesNotContain("define i32 @main(", result.LlvmIr!, StringComparison.Ordinal);
    }

    const string Arbitrary = """
        module Shop

        import Sable.Testing

        @derive(Arbitrary, Debug)
        struct Order { var id: Int; var lines: String[>..] }

        @fuzz
        fn orders(_ order: Order, discount percent: U8) throws {
            if order.lines.count == 2 && percent == 9 {
                fatalError("two lines")
            }
        }

        struct Suite {
            @fuzz(mutator: longer)
            static fn bytes(_ data: U8[:]) {
            }
        }

        fn longer(_ input: U8[:], _ seed: U64) -> U8[>..] {
            var more = input.toArray()
            more.append(U8.truncating(seed))
            return more
        }

        @test
        fn passes() {
        }
        """;

    /// <summary>A target of values is called with what its bytes choose; a mutator is exported when there is one.</summary>
    [Fact]
    public void ATargetTakesArbitraryValuesAndAMutator() {
        var result = Compile(Arbitrary, TestKind.Fuzz);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(["sable_harness_fuzz_begin", "sable_harness_fuzz_mutate", "sable_harness_fuzz_mutated", "sable_harness_fuzz_push", "sable_harness_fuzz_run"],
            result.Sil!.Exports.Values.Order(StringComparer.Ordinal));
        if (!CanBuild || !FuzzBuild.HasSanitizers || NativeTarget.Host.Platform is not (NativePlatform.Linux or NativePlatform.MacOS)) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-fuzz-values-").FullName;
        try {
            var replay = Path.Combine(directory, "replay");
            var build = FuzzBuild.Build(result.LlvmIr!, replay, FuzzEngine.Replay);
            Assert.True(build.Succeeded, build.Output);

            // id 0; two empty lines, each after a 1; the end; 9: a length of 4 bits, then they.
            var fails = Path.Combine(directory, "fails");
            File.WriteAllBytes(fails, [0, 0, 1, 0, 1, 0, 0, 4, 9]);
            var passes = Path.Combine(directory, "passes");
            File.WriteAllBytes(passes, []);
            var outcomes = FuzzCommand.Replay(replay, 0, [passes, fails]);
            Assert.Null(outcomes[0].Report);
            Assert.Equal("fatal error: two lines at test.sb:11:9", FuzzCommand.Signature(outcomes[1].Report!));
            Assert.Contains("called with Order { id: 0, lines: [\"\", \"\"] }, 9", outcomes[1].Report!, StringComparison.Ordinal);
        } finally {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>'sable test' runs what 'sable fuzz' kept: each failure a test, the corpus and the seeds one more.</summary>
    [Fact]
    public void SavedInputsAreRunBySableTest() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-fuzz-tests-").FullName;
        var console = Console.Out;
        try {
            var file = Path.Combine(directory, "t.sb");
            File.WriteAllText(file, Targets + "\n@test\nfn passes() {\n}\n");
            var layout = FuzzLayout.For(Path.Combine(directory, "target"), "Bytes.findsFuzz");
            Directory.CreateDirectory(layout.Crashes);
            Directory.CreateDirectory(layout.Corpus);
            Directory.CreateDirectory(Path.Combine(directory, "fuzz", "Bytes.throwsOnOne"));
            File.WriteAllText(Path.Combine(layout.Crashes, "crash-1"), "FUZZ");
            File.WriteAllText(Path.Combine(layout.Crashes, "crash-2"), "fixed");
            File.WriteAllText(Path.Combine(layout.Corpus, "a"), "abc");
            File.WriteAllText(Path.Combine(directory, "fuzz", "Bytes.throwsOnOne", "seed"), "xy");

            var captured = new StringWriter();
            Console.SetOut(captured);
            var code = Command.Find("test")!.Run([file]);
            Console.SetOut(console);
            var text = captured.ToString();
            Assert.Equal(1, code);
            Assert.Contains("test Bytes.findsFuzz(crash-1) ... FAILED", text, StringComparison.Ordinal);
            Assert.Contains("found it", text, StringComparison.Ordinal);
            Assert.Contains("crash-1", text, StringComparison.Ordinal);
            Assert.Contains("test Bytes.findsFuzz(crash-2) ... ok", text, StringComparison.Ordinal);
            Assert.Contains("test Bytes.findsFuzz(corpus) ... ok", text, StringComparison.Ordinal);
            Assert.Contains("test Bytes.throwsOnOne(corpus) ... ok", text, StringComparison.Ordinal);
            Assert.Contains("4 passed; 1 failed", text, StringComparison.Ordinal);

            // With nothing kept, the targets have no tests.
            Directory.Delete(Path.Combine(directory, "target"), true);
            Directory.Delete(Path.Combine(directory, "fuzz"), true);
            captured = new StringWriter();
            Console.SetOut(captured);
            Assert.Equal(0, Command.Find("test")!.Run([file]));
            Console.SetOut(console);
            Assert.Contains("1 passed; 0 failed", captured.ToString(), StringComparison.Ordinal);
        } finally {
            Console.SetOut(console);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void FailuresAreToldApartByWhatTheySay() {
        Assert.Equal("fatal error: found it at t.sb:8:9", FuzzCommand.Signature("fatal error: found it\n  at t.sb:8:9\n  at Bytes.findsFuzz\n"));
        Assert.Equal("SUMMARY: AddressSanitizer: heap-buffer-overflow strings.c:41 in sable_string_slice",
            FuzzCommand.Signature("==1==ERROR: AddressSanitizer: heap-buffer-overflow on address 0x1\n    #0 0x2 in f\nSUMMARY: AddressSanitizer: heap-buffer-overflow strings.c:41 in sable_string_slice\n"));
        Assert.Equal("runtime error: shift exponent 64 is too large for 64-bit type 'uint64_t'",
            FuzzCommand.Signature("arrays.c:12:7: runtime error: shift exponent 64 is too large for 64-bit type 'uint64_t'\n"));
        Assert.Equal("ERROR: libFuzzer: timeout after 20 seconds", FuzzCommand.Signature("ALARM: working on the last Unit for 20 seconds\n==9== ERROR: libFuzzer: timeout after 20 seconds\n"));
        Assert.Equal("something else", FuzzCommand.Signature("\nsomething else\nand more"));
    }

    /// <summary>
    ///     A saved input that fails is found again: the replay build runs each
    ///     input once, under the sanitizers, and goes on past the one that
    ///     brings it down; what it said is its report. What a target throws
    ///     fails it as a trap does.
    /// </summary>
    [Fact]
    public void ASavedFailingInputIsReplayed() {
        if (!CanBuild || !FuzzBuild.HasSanitizers || NativeTarget.Host.Platform is not (NativePlatform.Linux or NativePlatform.MacOS)) {
            return;
        }

        var result = Compile(Targets, TestKind.Fuzz);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var directory = Directory.CreateTempSubdirectory("sable-fuzz-replay-").FullName;
        try {
            var replay = Path.Combine(directory, "build", "replay");
            var build = FuzzBuild.Build(result.LlvmIr!, replay, FuzzEngine.Replay);
            Assert.True(build.Succeeded, build.Output);

            string Input(string name, string text) {
                var path = Path.Combine(directory, name);
                File.WriteAllText(path, text);
                return path;
            }

            var passes = Input("passes", "ab");
            var crashes = Input("crash-fuzz", "FUZZ");
            var one = Input("one", "x");

            var first = FuzzCommand.Replay(replay, 0, [passes, crashes, one]);
            Assert.Equal([passes, crashes, one], first.Select(static o => o.Input));
            Assert.Null(first[0].Report);
            Assert.Equal("fatal error: found it at test.sb:8:9", FuzzCommand.Signature(first[1].Report!));
            Assert.Null(first[2].Report);

            var second = FuzzCommand.Replay(replay, 1, [passes, crashes, one]);
            Assert.Equal([null, null], second.Take(2).Select(static o => o.Report));
            Assert.StartsWith("fatal error: Bytes.throwsOnOne threw: Bad.Odd", FuzzCommand.Signature(second[2].Report!), StringComparison.Ordinal);

            // Built again from the same IR, it is not built again.
            var stamp = File.GetLastWriteTimeUtc(replay);
            Assert.True(FuzzBuild.Build(result.LlvmIr!, replay, FuzzEngine.Replay).Succeeded);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(replay));
        } finally {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    ///     Where clang has no libFuzzer runtime — Apple's — fuzzing says so,
    ///     and how to get one, before it compiles anything; the replay still runs.
    /// </summary>
    [Fact]
    public void WithoutLibFuzzerItSaysSo() {
        var apple = FuzzBuild.MissingLibFuzzer("clang", (17, true), NativePlatform.MacOS);
        Assert.Contains("'clang' is Apple's clang 17, which has no libFuzzer runtime", apple, StringComparison.Ordinal);
        Assert.Contains("brew install llvm", apple, StringComparison.Ordinal);
        Assert.Contains("sable fuzz --replay", apple, StringComparison.Ordinal);
        Assert.Contains("libclang-rt", FuzzBuild.MissingLibFuzzer("clang", (18, false), NativePlatform.Linux), StringComparison.Ordinal);

        // Run for real only where it cannot fuzz: no fuzzer runs in a test.
        if (!CanBuild || FuzzBuild.HasLibFuzzer) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-fuzz-none-").FullName;
        var errors = Console.Error;
        try {
            var file = Path.Combine(directory, "t.sb");
            File.WriteAllText(file, Targets);
            var captured = new StringWriter();
            Console.SetError(captured);
            var code = Command.Find("fuzz")!.Run([file, "findsFuzz", "--duration", "1s"]);
            Assert.Equal(1, code);
            Assert.Contains("has no libFuzzer runtime", captured.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(directory, "target")));
        } finally {
            Console.SetError(errors);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TheCommandIsListed() {
        Assert.NotNull(Command.Find("fuzz"));
        Assert.Contains("usage: sable fuzz [<file.sb>...] [<target>] [--duration <time>]", new FuzzCommand().Usage, StringComparison.Ordinal);
    }
}
