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
    ///     Unoptimised: on Linux and Windows only such a build keeps the
    ///     program's functions where a backtrace finds their names
    ///     (<see cref="NativeBuild.ForTarget" />'s namesFrames).
    /// </summary>
    [Fact]
    public void SableBacktraceChoosesWhichFramesATrapShows() {
        if (!CanBuild) {
            return;
        }

        var source = Program("", "    fatalError(\"stop\")");
        var off = BuildAndRun(source, optimise: false, environment: new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "0" });
        Assert.Equal("fatal error: stop\n  at test.sb:5:5\n", off.Stderr);

        var on = BuildAndRun(source, optimise: false, environment: new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "1" });
        Assert.Contains("\nbacktrace:\n", on.Stderr, StringComparison.Ordinal);
        Assert.Contains("fatalError", on.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("sable_trap_with", on.Stderr, StringComparison.Ordinal);

        var full = BuildAndRun(source, optimise: false, environment: new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "full" });
        Assert.Contains("sable_trap_with", full.Stderr, StringComparison.Ordinal);
        Assert.Contains("fatalError", full.Stderr, StringComparison.Ordinal);
    }

    // ---- An error main throws ------------------------------------------

    const string BadError = """
                            union Bad: Error {
                                Oops(Int)

                                fn description() -> String { return "it went bad" }
                            }

                            fn go(_ n: Int) throws {
                                if n > 2 {
                                    throw Bad.Oops(n)
                                }
                            }
                            """;

    /// <summary>
    ///     An error that leaves main is a trap, said as one with the place of
    ///     the 'try' it left from, but the program ends with status 1, as
    ///     Rust's and Swift's do — not with 0 and nothing said.
    /// </summary>
    [Fact]
    public void AnErrorThatLeavesMainIsReportedAsUncaught() {
        var run = AssertAgree($$"""
                                module Test
                                {{BadError}}

                                @main
                                fn main() throws {
                                    try go(1)
                                    print("before")
                                    try go(5)
                                    print("unreached")
                                }
                                """);
        Assert.Equal("before\n", run.Stdout);
        Assert.Equal("fatal error: uncaught error: it went bad\n  at test.sb:18:9\n", run.Stderr);
        if (CanBuild) {
            Assert.Equal(1, run.ExitCode);
        }
    }

    /// <summary>
    ///     A 'throw' in main itself, of a type that does not describe itself
    ///     as an Error does: written out, as interpolation writes it.
    /// </summary>
    [Fact]
    public void AnErrorThrownInMainItselfIsReportedWhereItIsThrown() {
        var run = AssertAgree("""
                              module Test

                              struct Code {
                                  var value: Int
                              }

                              @main
                              fn main() throws(Code) {
                                  print("start")
                                  throw Code(value: 7)
                              }
                              """);
        Assert.Equal("start\n", run.Stdout);
        Assert.StartsWith("fatal error: uncaught error: ", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("7", run.Stderr, StringComparison.Ordinal);
        Assert.EndsWith("  at test.sb:10:5\n", run.Stderr, StringComparison.Ordinal);
    }

    /// <summary>An async main's error, thrown after it has waited, is reported as a sync one's is.</summary>
    [Fact]
    public void AnErrorThatLeavesAnAsyncMainIsReportedAsUncaught() {
        var run = AssertAgree($$"""
                                module Test
                                {{BadError}}

                                fn later(_ n: Int) async throws -> Int {
                                    await Task.yield()
                                    try go(n)
                                    return n
                                }

                                @main
                                fn main() async throws {
                                    print(try await later(1))
                                    let n = try await later(4)
                                    print(n)
                                }
                                """);
        Assert.Equal("1\n", run.Stdout);
        Assert.StartsWith("fatal error: uncaught error: it went bad\n  at test.sb:23:", run.Stderr, StringComparison.Ordinal);
        if (CanBuild) {
            Assert.Equal(1, run.ExitCode);
        }
    }

    /// <summary>An error caught in main is not uncaught: only one that would leave it is.</summary>
    [Fact]
    public void AnErrorCaughtInMainIsNotReported() =>
        Assert.Equal(
            "caught\nend\n",
            AssertAgree($$"""
                          module Test
                          {{BadError}}

                          @main
                          fn main() throws {
                              do {
                                  try go(9)
                              } catch {
                                  print("caught")
                              }
                              print("end")
                          }
                          """).Stdout
        );

    // ---- The panic hook --------------------------------------------------

    static string Hooked(string declarations, string body) =>
        $"module Test\nimport Sable.Diagnostics\n{declarations}\n@main\nfn main() {{\n{body}\n}}\n";

    /// <summary>
    ///     A hook set says the trap in place of the report, given its
    ///     message and place; the program still ends as a trap ends it.
    /// </summary>
    [Fact]
    public void APanicHookSaysATrapInPlaceOfTheReport() {
        var run = AssertAgree(Hooked("", """
                let name = "mine"
                Panic.setHook { info in
                    print("\(name): \(info.message) at \(info.file) line \(info.line) column \(info.column)")
                }
                print("before")
                fatalError("the end")
            """));
        Assert.Equal("before\nmine: the end at test.sb line 11 column 5\n", run.Stdout);
        Assert.Equal("", run.Stderr);
        if (CanBuild) {
            Assert.Equal(134, run.ExitCode);
        }
    }

    /// <summary>
    ///     Every kind of trap goes through it: one the compiled code makes
    ///     (an index out of range, overflow), one the runtime makes, a
    ///     none unwrapped, and an error main threw, which keeps its status 1.
    /// </summary>
    [Theory]
    [InlineData("    let values = [1, 2]\n    print(values[blackBox(5)])", "index out of range")]
    [InlineData("    print(blackBox(Int.max) + 1)", "overflow")]
    [InlineData("    let maybe: Int? = blackBox(none)\n    print(maybe!)", "none")]
    [InlineData("    precondition(index: 3, inBounds: 2)", "index 3 is out of range for count 2")]
    public void EveryKindOfTrapGoesThroughTheHook(string body, string expected) {
        var run = AssertAgree(Hooked("", "    Panic.setHook { info in print(\"hook: \\(info.message)\") }\n" + body));
        Assert.StartsWith("hook: ", run.Stdout, StringComparison.Ordinal);
        Assert.Contains(expected, run.Stdout, StringComparison.Ordinal);
        Assert.Equal("", run.Stderr);
    }

    [Fact]
    public void AnUncaughtErrorGoesThroughTheHookAndEndsWithStatusOne() {
        var run = AssertAgree($$"""
                                module Test
                                import Sable.Diagnostics
                                {{BadError}}

                                @main
                                fn main() throws {
                                    Panic.setHook { info in print("hook: \(info.message) at \(info.site)") }
                                    try go(3)
                                }
                                """);
        Assert.Equal("hook: uncaught error: it went bad at test.sb:18:9\n", run.Stdout);
        Assert.Equal("", run.Stderr);
        if (CanBuild) {
            Assert.Equal(1, run.ExitCode);
        }
    }

    /// <summary>
    ///     takeHook gives the hook back and leaves the default report; with
    ///     none set, it gives the default report itself, so a hook can be
    ///     put in front of it and still call it.
    /// </summary>
    [Fact]
    public void TakingTheHookLeavesTheDefaultReport() {
        var run = AssertAgree(Hooked("", """
                Panic.setHook { info in print("not this") }
                let _ = Panic.takeHook()
                fatalError("the end")
            """));
        Assert.Equal("", run.Stdout);
        Assert.Equal("fatal error: the end\n  at test.sb:8:5\n", run.Stderr);
    }

    [Fact]
    public void AHookCanCallTheOneItReplaced() {
        var run = AssertAgree(Hooked("", """
                let previous = Panic.takeHook()
                Panic.setHook { info in
                    print("first: \(info.message)")
                    previous(info)
                }
                fatalError("the end")
            """));
        Assert.Equal("first: the end\n", run.Stdout);
        Assert.Equal("fatal error: the end\n  at test.sb:11:5\n", run.Stderr);
    }

    /// <summary>A trap in the hook falls back to the default report: the trap the hook was given, then its own.</summary>
    [Fact]
    public void ATrapInTheHookFallsBackToTheDefaultReport() {
        var run = AssertAgree(Hooked("", """
                Panic.setHook { info in
                    print("in the hook")
                    fatalError("again")
                }
                fatalError("first")
            """));
        Assert.Equal("in the hook\n", run.Stdout);
        Assert.Equal("fatal error: first\n  at test.sb:10:5\nfatal error: the panic hook trapped: again\n  at test.sb:8:9\n", run.Stderr);
    }

    /// <summary>A hook set and never used is let go of as the program ends: nothing it holds is left alive.</summary>
    [Fact]
    public void AHookNeverUsedIsLetGoOf() =>
        Prints(
            "done\n",
            "import Sable.Diagnostics\n\nclass Note {\n    var text = \"note\"\n}\n",
            """
                let note = Note()
                Panic.setHook { info in print(note.text) }
                Panic.setHook { info in print(note.text + "!") }
                print("done")
            """
        );

    /// <summary>
    ///     Tasks trapping at once on the pool's threads: the first says it,
    ///     through the hook, which runs once, on its thread; the others wait
    ///     for the program to end.
    /// </summary>
    [Fact]
    public void TrapsOnSeveralThreadsAtOnceRunTheHookOnce() {
        if (!CanBuild) {
            return;
        }

        var source = """
                     module Test
                     import Sable.Diagnostics

                     fn fail(_ i: Int) async -> Int {
                         var total = 0
                         for k in 0..<10_000 {
                             total = total + k % 7
                         }
                         precondition(total < 0, "task \(i)")
                         return total
                     }

                     @main
                     fn main() async {
                         let label = "hook"
                         Panic.setHook { info in
                             print("\(label): \(info.message.hasPrefix("precondition failed: task"))")
                         }
                         var tasks: Task<Int>[>..] = []
                         for i in 0..<16 {
                             tasks.append(Task.spawn { await fail(i) })
                         }
                         for task in tasks {
                             print(await task.value)
                         }
                     }
                     """;
        for (var round = 0; round < 3; round++) {
            var run = BuildAndRun(source);
            Assert.Equal("hook: true\n", run.Stdout);
            Assert.Equal("", run.Stderr);
            Assert.Equal(134, run.ExitCode);
        }
    }

    // ---- The debugger ----------------------------------------------------

    /// <summary>With no debugger attached, isAttached is false and break() does nothing: the program goes on.</summary>
    [Fact]
    public void WithNoDebuggerBreakDoesNothing() =>
        Prints(
            "false\nafter\n",
            "import Sable.Diagnostics\n",
            """
                print(Debugger.isAttached)
                Debugger.break()
                print("after")
            """
        );

    // ---- Lines -----------------------------------------------------------

    /// <summary>
    ///     A debug build names each frame of a trap's backtrace by its line —
    ///     the program's and the library's — from its line table (DWARF, or
    ///     the PDB on Windows), and so does Backtrace.capture(); a build
    ///     without -g by symbol and offset, as before.
    /// </summary>
    [Fact]
    public void ADebugBuildNamesEachFrameByItsLine() {
        if (!CanBuild) {
            return;
        }

        var source = """
                     module Test
                     import Sable.Diagnostics

                     fn inner(_ n: Int) -> Int {
                         precondition(n < 3, "small")
                         return n
                     }

                     fn outer(_ n: Int) -> Int {
                         return inner(n) + 1
                     }

                     @main
                     fn main() {
                         for frame in Backtrace.capture().frames {
                             if frame.contains("Test.main") {
                                 print(frame)
                             }
                         }
                         print(outer(1))
                         print(outer(5))
                     }
                     """;
        var trace = new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "1" };
        var debug = BuildAndRunDebug(source, trace);
        Assert.StartsWith($"sable.Test.main at test.sb:15{(OperatingSystem.IsWindows() ? "" : ":18")}\n2\n", debug.Stdout, StringComparison.Ordinal);
        Assert.Contains($"  1  sable.Test.inner at test.sb:5{(OperatingSystem.IsWindows() ? "" : ":5")}\n", debug.Stderr, StringComparison.Ordinal);
        Assert.Contains($"  2  sable.Test.outer at test.sb:10{(OperatingSystem.IsWindows() ? "" : ":12")}\n", debug.Stderr, StringComparison.Ordinal);
        Assert.Contains($"  3  sable.Test.main at test.sb:21{(OperatingSystem.IsWindows() ? "" : ":11")}\n", debug.Stderr, StringComparison.Ordinal);
        Assert.Contains("precondition", debug.Stderr, StringComparison.Ordinal);
        Assert.Contains(" at lib/Sable.Core/Assertions.sb:", debug.Stderr.Replace('\\', '/'), StringComparison.Ordinal);

        var plain = BuildAndRun(source, optimise: false, environment: trace);
        Assert.Contains("sable.Test.inner + ", plain.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("inner at test.sb", plain.Stderr, StringComparison.Ordinal);
    }

    /// <summary>Built with -g, as <c>sable build -g</c> builds, and run.</summary>
    static Execution BuildAndRunDebug(string source, IReadOnlyDictionary<string, string> environment) {
        var directory = Directory.CreateTempSubdirectory("sable-lines-");
        try {
            File.WriteAllText(Path.Combine(directory.FullName, "test.sb"), source);
            var result = Pipeline.Compile(new SourceText(source, "test.sb"), directory.FullName);
            Assert.True(result.Succeeded, string.Join("\n", result.Errors));
            var executable = Platform.Executable(Path.Combine(directory.FullName, "test"));
            var build = NativeBuild.Build(result.LlvmIr!, executable, debug: true);
            Assert.True(build.Succeeded, build.Output);

            var start = new System.Diagnostics.ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
            foreach (var (name, value) in environment) {
                start.Environment[name] = value;
            }

            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new(stdout.Result, stderr, process.ExitCode);
        } finally {
            directory.Delete(true);
        }
    }
}
