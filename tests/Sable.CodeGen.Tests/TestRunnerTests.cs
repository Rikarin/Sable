using Sable.Driver.Commands;
using Sable.Symbols;
using static Sable.CodeGen.Tests.Harnesses;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable test</c> (docs/05 §5.1): the harness the compiler writes
///     around the <c>@test</c> functions, run natively — each test in a
///     process of its own — and in the interpreter, in one. The command
///     prints to the console, which other tests take over to read it: so
///     not at the same time as those.
/// </summary>
[Collection("Console")]
public sealed class TestRunnerTests {
    const string Suite = """
        module Suite

        import Sable.Testing

        union Oops { Bad }

        fn double(_ n: Int) -> Int { return n * 2 }

        @test
        fn doubling() {
            assertEqual(double(21), 42)
            assert(double(0) == 0, "zero doubles to zero")
        }

        @test
        fn wrongAnswer() {
            print("this output is shown, as the test failed")
            assertEqual(double(2), 5)
        }

        @test
        fn outOfRange() {
            let xs = [1, 2, 3]
            print(xs[3])
        }

        @test
        fn throwing() throws(Oops) {
            throw Oops.Bad
        }

        struct Checks {
            @test
            static fn inAType() {
                assertEqual("a" + "b", "ab")
            }
        }

        @test
        fn afterTheTrap() {
            assertNotEqual(1, 2)
        }

        @main
        fn main() {
            print("the program's own main, which a test build does not run")
        }
        """;

    [Fact]
    public void EachTestPassesOrFailsOnItsOwn() {
        if (!CanBuild) {
            return;
        }

        var run = Native(Suite);
        Assert.Equal(1, run.ExitCode);
        Assert.StartsWith("running 6 tests\n", run.Stdout, StringComparison.Ordinal);
        Assert.Matches(@"test Suite\.doubling \.\.\. ok \(\d+\.\d\d (ns|µs|ms|s)\)\n", run.Stdout);
        Assert.Contains("test Suite.wrongAnswer ... FAILED (", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("test Suite.outOfRange ... FAILED (", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("test Suite.throwing ... FAILED (", run.Stdout, StringComparison.Ordinal);

        // A trap ends its own test's process, and the tests after it run.
        Assert.Contains("test Suite.Checks.inAType ... ok (", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("test Suite.afterTheTrap ... ok (", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("the program's own main", run.Stdout, StringComparison.Ordinal);

        Assert.Contains("---- Suite.outOfRange (test.sb:22) ----\n    fatal error: array index out of range\n      at test.sb:24:11\n    (exit status 134)\n", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("---- Suite.throwing (test.sb:28) ----\n    assertion failed: the test threw an error: Oops.Bad\n", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("failed:\n    Suite.wrongAnswer\n    Suite.outOfRange\n    Suite.throwing\n", run.Stdout, StringComparison.Ordinal);
        Assert.Matches(@"\ntest result: FAILED\. 3 passed; 3 failed; 0 filtered out; finished in [0-9.]+ (µs|ms|s)\n$", run.Stdout);
    }

    [Fact]
    public void AFailedAssertionShowsBothValuesAndTheTestsOutput() {
        if (!CanBuild) {
            return;
        }

        var run = Native(Suite, arguments: ["wrongAnswer"]);
        Assert.Contains(
            """
            ---- Suite.wrongAnswer (test.sb:16) ----
            stdout:
                this output is shown, as the test failed
                assertion failed: the values differ
                   left: 4
                  right: 5
                         ^ first difference at character 0

            """,
            run.Stdout,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("try!", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AllPassingExitsZero() {
        if (!CanBuild) {
            return;
        }

        var run = Native(Suite, arguments: ["doubling", "Checks", "after"]);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("running 3 tests\n", run.Stdout, StringComparison.Ordinal);
        Assert.Matches(@"test result: ok\. 3 passed; 0 failed; 3 filtered out; finished in ", run.Stdout);
        Assert.Equal("", run.Stderr);
    }

    [Fact]
    public void FiltersSelectByPartOfTheNameOrAllOfIt() {
        if (!CanBuild) {
            return;
        }

        var list = Native(Suite, arguments: ["--list", "o"]);
        Assert.Equal(0, list.ExitCode);
        Assert.Equal(
            """
            Suite.doubling: test (test.sb:10)
            Suite.wrongAnswer: test (test.sb:16)
            Suite.outOfRange: test (test.sb:22)
            Suite.throwing: test (test.sb:28)
            4 tests

            """,
            list.Stdout
        );

        var exact = Native(Suite, arguments: ["--list", "--exact", "Suite.doubling", "Suite.Checks"]);
        Assert.Equal("Suite.doubling: test (test.sb:10)\n1 test\n", exact.Stdout);

        var none = Native(Suite, arguments: ["nothing matches this"]);
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("running 0 tests\n", none.Stdout, StringComparison.Ordinal);
        Assert.Contains("0 passed; 0 failed; 6 filtered out", none.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownOptionIsAUsageError() {
        if (!CanBuild) {
            return;
        }

        var run = Native(Suite, arguments: ["--frobnicate"]);
        Assert.Equal(2, run.ExitCode);
        Assert.StartsWith("unknown option '--frobnicate'\nusage:", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTestRunsOneInThisProcess() {
        if (!CanBuild) {
            return;
        }

        Assert.Equal(0, Native(Suite, arguments: ["--run-test", "Suite.doubling"]).ExitCode);
        var failed = Native(Suite, arguments: ["--run-test", "Suite.wrongAnswer"]);
        Assert.Equal(134, failed.ExitCode);
        Assert.StartsWith("assertion failed: the values differ\n", failed.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, Native(Suite, arguments: ["--run-test", "Suite.nope"]).ExitCode);
    }

    [Fact]
    public void TextIsComparedLineByLine() {
        if (!CanBuild) {
            return;
        }

        var run = Native("""
            module Lines
            import Sable.Testing

            @test
            fn report() {
                assertEqualText("title\nalpha\nbeta\ngamma\n", "title\nalpha\nBETA\ngamma\ndelta\n")
            }
            """);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains(
            """
                assertion failed: the text differs (-left +right)
                  title
                  alpha
                - beta
                + BETA
                  gamma
                + delta

            """,
            run.Stdout,
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     In one process the harness prints no times, so the interpreter and
    ///     a native build print the same; it is what the interpreter does on
    ///     its own, with no executable to start again.
    /// </summary>
    [Fact]
    public void InOneProcessTheInterpreterAndTheNativeBuildAgree() {
        var passing = Suite.Replace("@test\nfn wrongAnswer", "fn wrongAnswer")
            .Replace("@test\nfn outOfRange", "fn outOfRange")
            .Replace("@test\nfn throwing", "fn throwing");
        var (output, errors, trap) = Interpret(passing);
        Assert.Null(trap);
        Assert.Equal("", errors);
        Assert.Equal(
            """
            running 3 tests
            test Suite.doubling ... ok
            test Suite.Checks.inAType ... ok
            test Suite.afterTheTrap ... ok

            test result: ok. 3 passed; 0 failed; 0 filtered out

            """,
            output
        );

        // A failure stops a run in one process: there is nothing to isolate it.
        var (failedOutput, failedErrors, failedTrap) = Interpret(Suite, ["doubling", "wrong"]);
        Assert.Equal("running 2 tests\ntest Suite.doubling ... ok\nthis output is shown, as the test failed\n", failedOutput);
        Assert.Equal("'try!' caught an error", failedTrap?.Message);
        Assert.StartsWith("assertion failed: the values differ\n", failedErrors, StringComparison.Ordinal);

        if (!CanBuild) {
            return;
        }

        var native = Native(passing, arguments: ["--in-process"]);
        Assert.Equal(0, native.ExitCode);
        Assert.Equal(output, native.Stdout);

        var nativeFailed = Native(Suite, arguments: ["--in-process", "doubling", "wrong"]);
        Assert.Equal(134, nativeFailed.ExitCode);
        Assert.Equal(failedOutput, nativeFailed.Stdout);
        Assert.Equal($"{failedErrors}{failedTrap!.Report}", nativeFailed.Stderr);
    }

    [Fact]
    public void TestsInSeveralFilesAndModulesShareOneHarness() {
        if (!CanBuild) {
            return;
        }

        var run = Native([
            new SourceText("module First\nimport Sable.Testing\n@test\nfn one() { assert(true) }\nfn helper() -> Int { return 7 }\n", "first.sb"),
            new SourceText("module Second\nimport Sable.Testing\nimport First\n@test\nfn two() { assertEqual(helperValue(), 7) }\n", "second.sb"),
            new SourceText("module First\nimport Sable.Testing\npublic fn helperValue() -> Int { return helper() }\n@test\nfn three() { assertEqual(helper(), 7) }\n", "third.sb"),
        ]);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("test First.one ... ok", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("test First.three ... ok", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("test Second.two ... ok", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameOfTheProgramsOwnDoesNotClashWithTheHarness() {
        if (!CanBuild) {
            return;
        }

        // The program's module declares what Sable.Testing does: the
        // harness's entry point imports nothing else of the program.
        var run = Native("""
            module Clash
            import Sable.Testing
            fn runTests() -> Int { return 99 }
            struct TestCase { var x: Int }
            @test
            fn fine() { assertEqual(runTests(), 99) }
            """);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("1 passed; 0 failed", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AProgramWithNoTestsRunsNone() {
        if (!CanBuild) {
            return;
        }

        var run = Native("module Empty\nfn helper() { }\n");
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("running 0 tests\n", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryBuildIgnoresTheTests() {
        var result = Pipeline.Compile(new SourceText(Suite, "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal("Suite.main", result.Sil!.EntryPoint);
        Assert.Contains("the program's own main", Programs.AssertAgree(Suite).Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHarnessIsWrittenInSable() {
        var tests = Compilation.Create([
            SyntaxTree.Parse(new SourceText(Suite, "test.sb")),
            ..Library.For(SyntaxTree.Parse(new SourceText(Suite, "test.sb"))),
        ]).GetTestFunctions(TestKind.Test);
        Assert.Equal(
            ["Suite.doubling", "Suite.wrongAnswer", "Suite.outOfRange", "Suite.throwing", "Suite.Checks.inAType", "Suite.afterTheTrap"],
            tests.Select(static t => t.QualifiedName)
        );
        Assert.True(tests[3].Throws);

        var files = TestHarness.Write(tests, TestKind.Test);
        Assert.Equal(2, files.Count);
        var list = files[0].Text.Text;
        Assert.StartsWith("module Suite\n", list, StringComparison.Ordinal);
        Assert.Contains("() => { Checks.inAType(); return none }", list, StringComparison.Ordinal);
        Assert.Contains("do { try throwing() } catch { return \"\\(error)\" }", list, StringComparison.Ordinal);
        var main = files[1].Text.Text;
        Assert.Contains($"module {TestHarness.HarnessModule}\n", main, StringComparison.Ordinal);
        Assert.Contains("import Suite.{ sableHarnessList0 }", main, StringComparison.Ordinal);
        Assert.Contains("all.append(TestCase { name: \"Suite.doubling\", location: \"test.sb:10\", run: list0[0], traps: none })", main, StringComparison.Ordinal);
        Assert.Contains("async fn sableHarnessMain() -> Int", main, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestCommandExitsAsTheTestsDo() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-test-command-");
        try {
            var file = Path.Combine(directory.FullName, "suite.sb");
            File.WriteAllText(file, Suite);
            var test = Command.Find("test")!;
            Assert.Equal(0, test.Run([file, "doubling"]));
            Assert.Equal(1, test.Run([file]));
            Assert.Equal(0, test.Run([file, "--list"]));
            Assert.Equal(2, test.Run([file, "--no-such-option"]));

            var broken = Path.Combine(directory.FullName, "broken.sb");
            File.WriteAllText(broken, "module Broken\nimport Sable.Testing\n@test\nfn takes(_ n: Int) { }\n");
            Assert.Equal(1, test.Run([broken]));
            Assert.Equal(1, test.Run([Path.Combine(directory.FullName, "missing.sb")]));
            Assert.Equal(1, test.Run([]));
        } finally {
            directory.Delete(true);
        }
    }

    [Fact]
    public void TheLibrarysOwnTestsPass() {
        if (!CanBuild) {
            return;
        }

        // Each module's tests are one program, as tools/stdlib-coverage.sh
        // builds them: tests/lib/<Module>/<Type>Tests.sb, every file of it.
        // A directory with no Sable files in it, such as the target/ a local
        // 'sable test --coverage' leaves, is not a module.
        var modules = Directory.GetDirectories(Path.Combine(Root, "tests/lib"))
            .Where(static d => Directory.EnumerateFiles(d, "*.sb", SearchOption.AllDirectories).Any())
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(modules);
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "tests/lib"), "*.sb"));
        foreach (var module in modules) {
            var files = Directory.GetFiles(module, "*.sb", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
            var run = Native([..files.Select(f => new SourceText(File.ReadAllText(f), Path.GetRelativePath(Root, f).Replace('\\', '/')))]);
            Assert.True(run.ExitCode == 0, module + "\n" + run.Stdout + run.Stderr);
            Assert.Matches(@"test result: ok\. \d+ passed; 0 failed; 0 filtered out", run.Stdout);
            Assert.Equal("", run.Stderr);
        }
    }
}
