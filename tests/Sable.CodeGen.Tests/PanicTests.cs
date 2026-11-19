using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Stopping the program on purpose — <c>fatalError</c>, <c>precondition</c>
///     and the rest of lib/Sable.Core/Assertions.sb, over the <c>trap</c>
///     builtin — natively and in the interpreter alike: a function that gives
///     <c>Never</c>, the caller's place as a default argument, and backtraces.
/// </summary>
public sealed class PanicTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    static void Traps(string expectedOutput, string expectedError, string declarations, string body) {
        var run = AssertAgree(Program(declarations, body));
        Assert.Equal(expectedOutput, run.Stdout);
        Assert.Equal(expectedError, run.Stderr);
    }

    [Fact]
    public void FatalErrorSaysWhyAndWhereItWasCalled() =>
        Traps(
            "before\n",
            "fatal error: the end\n  at test.sb:6:5\n",
            "",
            """
                print("before")
                fatalError("the end")
            """
        );

    [Fact]
    public void APreconditionThatFailsSaysSo() =>
        Traps(
            "",
            "fatal error: precondition failed: n is positive\n  at test.sb:6:5\n",
            "",
            """
                let n = -3
                precondition(n > 0, "n is positive")
            """
        );

    [Fact]
    public void APreconditionThatHoldsDoesNothing() =>
        Prints("ok\n", "", "    precondition(1 < 2)\n    print(\"ok\")");

    /// <summary>The report names the line of the check, in the function that made it, not Assertions.sb's.</summary>
    [Fact]
    public void AGuardHelperReportsTheLineOfTheCheck() =>
        Traps(
            "",
            "fatal error: index 4 is out of range for count 2\n  at test.sb:3:5\n",
            """
            fn second(_ values: Int[>..], _ i: Int) -> Int {
                precondition(index: i, inBounds: values.count)
                return values[i]
            }
            """,
            "    print(second([1, 2], 4))"
        );

    /// <summary>
    ///     A function that gives Never, called last in one that gives a value,
    ///     and standing for a value in an expression: nothing after it runs.
    /// </summary>
    [Fact]
    public void AFunctionThatGivesNeverEndsTheBranchItIsIn() =>
        Traps(
            "1\n-1\n2\n",
            "fatal error: no sign for 0\n  at test.sb:9:5\n",
            """
            fn sign(_ n: Int) -> Int {
                if n > 0 { return 1 }
                if n < 0 { return -1 }
                stop("no sign for \(n)")
            }

            fn stop(_ why: String) -> Never {
                fatalError(why)
            }
            """,
            """
                print(sign(5))
                print(sign(-5))
                let fallback: Int = 3 > 2 ? 2 : stop("unreached")
                print(fallback)
                print(sign(0))
            """
        );

    /// <summary>
    ///     A parameter that defaults to '#file', '#line', '#column' or
    ///     '#function' takes the call's place, as Swift's do: the default's
    ///     own place would name the same line every time.
    /// </summary>
    [Fact]
    public void APlaceAsADefaultIsTheCallersPlace() =>
        Prints(
            "test.sb:9:11 in main\ntest.sb:10:11 in main\nnamed\n",
            """
            fn here(file: String = #file, line: Int = #line, column: Int = #column, function: String = #function) -> String {
                return "\(file):\(line):\(column) in \(function)"
            }

            fn named(_ label: String = "named") -> String { return label }
            """,
            """
                print(here())
                print(here())
                print(named())
            """
        );

    [Fact]
    public void TodoAndUnimplementedSayWhichTheyAre() {
        Traps("", "fatal error: not yet implemented: parsing\n  at test.sb:5:5\n", "", "    todo(\"parsing\")");
        Traps("", "fatal error: not implemented\n  at test.sb:5:5\n", "", "    unimplemented()");
    }

    /// <summary>The interpreter has no frames of its own to give; a native program does.</summary>
    [Fact]
    public void ABacktraceHoldsTheFramesNatively() {
        var source = Program("", "    print(Runtime.backtraceFrames().count > 0)");
        Assert.Equal("false\n", Interpret(source).Output);
        if (CanBuild) {
            Assert.Equal("true\n", BuildAndRun(source).Stdout);
        }
    }

    /// <summary>
    ///     SABLE_BACKTRACE=1 shows the program's frames from the caller of the
    ///     runtime's trap; 'full' the runtime's own too; '0' none at all.
    /// </summary>
    [Fact]
    public void SableBacktraceChoosesWhichFramesATrapShows() {
        if (!CanBuild) {
            return;
        }

        var source = Program("", "    fatalError(\"stop\")");
        var off = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "0" });
        Assert.Equal("fatal error: stop\n  at test.sb:5:5\n", off.Stderr);

        var on = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "1" });
        Assert.Contains("\nbacktrace:\n", on.Stderr, StringComparison.Ordinal);
        Assert.Contains("fatalError", on.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("sable_trap_with", on.Stderr, StringComparison.Ordinal);

        var full = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "full" });
        Assert.Contains("sable_trap_with", full.Stderr, StringComparison.Ordinal);
        Assert.Contains("fatalError", full.Stderr, StringComparison.Ordinal);
    }
}
