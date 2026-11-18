using Sable.Driver.Commands;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     What #205 added to <c>sable test</c>: async tests, a thrown error in
///     the failure's report, tests that must trap, and the runner of
///     <c>sable test</c> itself — several tests at once, each with a time limit.
/// </summary>
[Collection("Console")]
public sealed class TestRunnerMoreTests {
    const string Suite = """
        module Suite

        import Sable.Testing

        enum Oops: Error { bad, worse }

        async fn compute() -> Int { return 21 }

        @test
        async fn awaits() {
            let n = await compute()
            assertEqual(n * 2, 42)
        }

        @test
        async fn awaitsAndThrows() throws(Oops) {
            let n = await compute()
            if n > 0 {
                throw Oops.worse
            }
        }

        @test(traps)
        fn outOfRange() {
            let xs = [1, 2, 3]
            print(xs[3])
        }

        @test(traps: "out of range")
        fn outOfRangeSaid() {
            let xs = [1, 2, 3]
            print(xs[3])
        }

        @test(traps: "overflow")
        fn theWrongTrap() {
            let xs = [1, 2, 3]
            print(xs[3])
        }

        @test(traps)
        fn returns() {
            assert(true)
        }

        @test(traps)
        fn anAssertionIsNotTheTrap() {
            assertEqual(1, 2)
        }
        """;

    const string Hangs = """

        @test
        fn hangs() {
            var n = 0
            while true {
                n = n + 1
                if n < 0 {
                    print(n)
                }
            }
        }
        """;

    static void AssertTheSuitesVerdicts(string output) {
        Assert.Contains("test Suite.awaits ... ok (", output, StringComparison.Ordinal);
        Assert.Contains("test Suite.outOfRange ... ok (", output, StringComparison.Ordinal);
        Assert.Contains("test Suite.outOfRangeSaid ... ok (", output, StringComparison.Ordinal);
        Assert.Contains("test Suite.awaitsAndThrows ... FAILED (", output, StringComparison.Ordinal);
        Assert.Contains("test Suite.theWrongTrap ... FAILED (", output, StringComparison.Ordinal);
        Assert.Contains("test Suite.returns ... FAILED (", output, StringComparison.Ordinal);
        Assert.Contains("test Suite.anAssertionIsNotTheTrap ... FAILED (", output, StringComparison.Ordinal);

        // What the test threw is in its report.
        Assert.Contains("---- Suite.awaitsAndThrows (test.sb:16) ----\n    assertion failed: the test threw an error: worse\n", output, StringComparison.Ordinal);
        Assert.Contains("    fatal error: array index out of range\n      at test.sb:38:11\n    the test trapped, but not with 'overflow'\n    (exit status 134)\n", output, StringComparison.Ordinal);
        Assert.Contains("---- Suite.returns (test.sb:42) ----\n    the test was to trap, and it returned\n    (exit status 0)\n", output, StringComparison.Ordinal);
        Assert.Contains("---- Suite.anAssertionIsNotTheTrap (test.sb:47) ----\n    assertion failed: the values differ\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHarnessRunsAsyncTestsAndTestsThatMustTrap() {
        if (!CanBuild) {
            return;
        }

        var run = Native(Suite);
        Assert.Equal(1, run.ExitCode);
        AssertTheSuitesVerdicts(run.Stdout);
        Assert.Contains("test result: FAILED. 3 passed; 4 failed; 0 filtered out;", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void InOneProcessATestThatMustTrapIsIgnored() {
        var (output, errors, trap) = Interpret(Suite, ["Suite.awaits", "Suite.outOfRange", "Suite.outOfRangeSaid", "--exact"]);
        Assert.Null(trap);
        Assert.Equal("", errors);
        Assert.Equal(
            """
            running 3 tests
            test Suite.awaits ... ok
            test Suite.outOfRange ... ignored (it must trap, which only a process of its own can)
            test Suite.outOfRangeSaid ... ignored (it must trap, which only a process of its own can)

            test result: ok. 1 passed; 0 failed; 2 ignored; 4 filtered out

            """,
            output);
    }

    [Fact]
    public void TheHarnessDescribesItsTestsForTheRunner() {
        if (!CanBuild) {
            return;
        }

        var run = Native(Suite, arguments: ["--describe", "outOfRange"]);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Suite.outOfRange\ttest.sb:24\t\nSuite.outOfRangeSaid\ttest.sb:30\tout of range\n", run.Stdout);
        Assert.Equal(
            [new("Suite.outOfRange", "test.sb:24", ""), new("Suite.outOfRangeSaid", "test.sb:30", "out of range"), new("Suite.awaits", "test.sb:10", null)],
            Sable.Driver.TestRunner.Parse(run.Stdout + "Suite.awaits\ttest.sb:10\t-\n"));
    }

    /// <summary>
    ///     <c>sable test</c> runs the processes itself: several at once, and
    ///     a test past the time limit is stopped and failed, and the others
    ///     still pass or fail as they do.
    /// </summary>
    [Fact]
    public void TheTestCommandRunsTestsAtOnceWithATimeLimit() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-test-runner-");
        var output = new StringWriter();
        var saved = Console.Out;
        try {
            var file = Path.Combine(directory.FullName, "test.sb");
            File.WriteAllText(file, Suite + Hangs);
            Console.SetOut(output);
            Assert.Equal(1, Command.Find("test")!.Run([file, "--jobs", "4", "--timeout", "3"]));
        } finally {
            Console.SetOut(saved);
            directory.Delete(true);
        }

        // The driver's lines end as the platform's do; the harness's in '\n'.
        var text = output.ToString().ReplaceLineEndings("\n");
        Assert.StartsWith("running 8 tests\n", text, StringComparison.Ordinal);
        AssertTheSuitesVerdicts(text);
        Assert.Contains("test Suite.hangs ... FAILED (", text, StringComparison.Ordinal);
        Assert.True(text.Contains("---- Suite.hangs (test.sb:51) ----\n    the test did not finish within 3", StringComparison.Ordinal), text);
        Assert.Contains("test result: FAILED. 3 passed; 5 failed; 0 filtered out; finished in ", text, StringComparison.Ordinal);

        // Failures are reported in the order the tests are declared, whatever order they finished in.
        Assert.True(text.IndexOf("---- Suite.awaitsAndThrows", StringComparison.Ordinal) < text.IndexOf("---- Suite.hangs", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTestCommandFiltersAsTheHarnessDoes() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-test-runner-");
        var output = new StringWriter();
        var saved = Console.Out;
        try {
            var file = Path.Combine(directory.FullName, "test.sb");
            File.WriteAllText(file, Suite);
            Console.SetOut(output);
            Assert.Equal(0, Command.Find("test")!.Run([file, "Suite.awaits", "Suite.outOfRangeSaid", "--exact", "-j", "1"]));
            Assert.Equal(2, Command.Find("test")!.Run([file, "--timeout", "never"]));
        } finally {
            Console.SetOut(saved);
            directory.Delete(true);
        }

        Assert.Contains("running 2 tests\n", output.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("test result: ok. 2 passed; 0 failed; 5 filtered out;", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HarnessOutBuildsTheHarnessAndRunsNothing() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-test-runner-");
        try {
            var file = Path.Combine(directory.FullName, "test.sb");
            File.WriteAllText(file, Suite);
            var harness = Path.Combine(directory.FullName, "harness");
            Assert.Equal(0, Command.Find("test")!.Run([file, "--harness-out", harness]));
            Assert.True(File.Exists(harness));

            // It is the harness: one test runs in it, as a debugger would start it.
            var (stdout, code) = PackageFixture.Execute(harness, "--run-test", "Suite.awaits");
            Assert.Equal(0, code);
            Assert.Equal("", stdout);
        } finally {
            directory.Delete(true);
        }
    }
}
