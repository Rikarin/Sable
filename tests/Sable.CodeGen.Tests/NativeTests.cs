using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Programs compiled to native code and run, with their output checked —
///     and checked against the SIL interpreter, which implements the same
///     semantics independently. Where the two disagree, one of them is wrong.
/// </summary>
/// <remarks>
///     These need a C compiler. Without one they pass vacuously, saying so in
///     the output, rather than failing a machine that cannot build binaries.
/// </remarks>
public sealed class NativeTests {
    static readonly bool CanBuild = NativeBuild.IsAvailable();

    sealed record Execution(string Stdout, string Stderr, int ExitCode);

    static Execution BuildAndRun(string source, bool optimise = true) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(result.LlvmIr!, executable, optimise);
            Assert.True(build.Succeeded, build.Output + "\n\n" + result.LlvmIr);

            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true }.Utf8();
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new(stdout.Result, stderr, process.ExitCode);
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }

    /// <summary>What the interpreter prints for the same program, and whether it trapped.</summary>
    static (string Output, SilTrap? Trap) Interpret(string source) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"));
        var interpreter = new SilInterpreter(result.Sil!);
        try {
            interpreter.Run(result.Sil!.EntryPoint!);
            return (interpreter.Output.ToString(), null);
        } catch (SilTrap trap) {
            return (interpreter.Output.ToString(), trap);
        }
    }

    /// <summary>Native and interpreted runs must print the same, and trap the same.</summary>
    static Execution AssertAgree(string source) {
        var native = BuildAndRun(source);
        var (output, trap) = Interpret(source);

        Assert.Equal(output, native.Stdout);
        if (trap is null) {
            Assert.Equal(0, native.ExitCode);
        } else {
            Assert.NotEqual(0, native.ExitCode);
            Assert.Equal(trap.Report, native.Stderr);
        }

        return native;
    }

    [Fact]
    public void HelloWorld() {
        if (!CanBuild) {
            return;
        }

        var run = AssertAgree(
            """
            module Hello

            @main
            fn main() {
                print("hello, world")
            }
            """
        );

        Assert.Equal("hello, world\n", run.Stdout);
    }

    [Fact]
    public void ArithmeticRecursionAndLoops() {
        if (!CanBuild) {
            return;
        }

        var run = AssertAgree(
            """
            module Numbers

            fn fib(_ n: Int) -> Int {
                if n < 2 { return n }
                return fib(n - 1) + fib(n - 2)
            }

            fn gcd(_ a: Int, _ b: Int) -> Int {
                var x = a
                var y = b
                while y != 0 {
                    let t = x % y
                    x = y
                    y = t
                }
                return x
            }

            fn collatz(_ start: Int) -> Int {
                var n = start
                var steps = 0
                while n != 1 {
                    n = n % 2 == 0 ? n / 2 : 3 * n + 1
                    steps += 1
                }
                return steps
            }

            @main
            fn main() {
                print(fib(25))
                print(gcd(1071, 462))
                print(collatz(27))
                print(-7 / 2)
                print(-7 % 2)
                print(1 << 40)
                print(-64 >> 3)
                print(~0)
            }
            """
        );

        Assert.Equal("75025\n21\n111\n-3\n-1\n1099511627776\n-8\n-1\n", run.Stdout);
    }

    [Fact]
    public void BooleansAndShortCircuiting() {
        if (!CanBuild) {
            return;
        }

        var run = AssertAgree(
            """
            module Logic

            fn safe(_ n: Int) -> Bool {
                return n != 0 && 100 / n > 10
            }

            @main
            fn main() {
                print(safe(0))
                print(safe(5))
                print(safe(50))
                print(!(true || false) || (true && !false))
            }
            """
        );

        Assert.Equal("false\ntrue\nfalse\ntrue\n", run.Stdout);
    }

    [Fact]
    public void FloatingPointPrintsAsTheInterpreterDoes() {
        if (!CanBuild) {
            return;
        }

        AssertAgree(
            """
            module Floats

            fn mean(_ a: F64, _ b: F64) -> F64 {
                let two: F64 = 2
                return (a + b) / two
            }

            @main
            fn main() {
                print(mean(1.5, 2.25))
                print(0.1 + 0.2)
                print(1.0 / 3.0)
                print(100000.0 * 100000.0 * 100000.0 * 100000.0 * 100000.0)
                print(-0.000012)
                print(1.0 / 3.0 > 0.333)
            }
            """
        );
    }

    [Fact]
    public void AWholeFloatKeepsItsPoint() {
        if (!CanBuild) {
            return;
        }

        // Shortest digits, laid out as Python's repr: a point in decimal for
        // exponents -4 through 15, scientific outside them.
        var run = AssertAgree(
            """
            module Points

            @main
            fn main() {
                print(100.0)
                print(4.0)
                print(-0.0)
                print(0.0)
                print(1e20)
                print(1e15)
                print(1e16)
                print(123456.75)
                print(0.0001)
                print(0.00001)
                print(-1.5e-7)
                print(0.1 + 0.2)
                let f: F32 = 3.0
                print("\(f) \(1.1 as F32) \(2.5)")
            }
            """
        );

        Assert.Equal(
            "100.0\n4.0\n-0.0\n0.0\n1e+20\n1000000000000000.0\n1e+16\n123456.75\n0.0001\n1e-05\n-1.5e-07\n"
            + "0.30000000000000004\n3.0 1.1 2.5\n",
            run.Stdout
        );
    }

    [Fact]
    public void LabelledJumps() {
        if (!CanBuild) {
            return;
        }

        var run = AssertAgree(
            """
            module Jumps

            @main
            fn main() {
                var i = 1
                outer: while i < 10 {
                    var j = 1
                    while j < 10 {
                        if i * j == 24 {
                            print(i * 100 + j)
                            break outer
                        }
                        j += 1
                    }
                    i += 1
                }

                var sum = 0
                var k = 0
                repeat {
                    k += 1
                    if k % 3 == 0 { continue }
                    sum += k
                } while k < 10
                print(sum)
            }
            """
        );

        Assert.Equal("308\n37\n", run.Stdout);
    }

    [Theory]
    [InlineData("print(9223372036854775807 + 1)", "integer overflow")]
    [InlineData("print(-9223372036854775807 - 2)", "integer overflow")]
    [InlineData("print(4611686018427387904 * 2)", "integer overflow")]
    [InlineData("let zero = 0\n    print(1 / zero)", "division by zero")]
    [InlineData("let zero = 0\n    print(1 % zero)", "division by zero")]
    [InlineData("let big = 64\n    print(1 << big)", "shift out of range")]
    public void TrapsStopTheProgramWithTheirReason(string body, string reason) {
        if (!CanBuild) {
            return;
        }

        var run = AssertAgree($"module Traps\n@main\nfn main() {{\n    print(\"before\")\n    {body}\n    print(\"after\")\n}}\n");

        // What was printed before the trap is not lost; nothing after it runs.
        Assert.Equal("before\n", run.Stdout);
        Assert.StartsWith($"fatal error: {reason}\n  at test.sb:", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void MainMayReturnTheExitCode() {
        if (!CanBuild) {
            return;
        }

        var run = BuildAndRun("module Exit\n@main\nfn main() -> Int { return 3 }\n");
        Assert.Equal(3, run.ExitCode);
    }

    [Fact]
    public void UnoptimisedCodeBehavesTheSame() {
        if (!CanBuild) {
            return;
        }

        const string source = """
                              module O0
                              fn sum(_ n: Int) -> Int {
                                  var total = 0
                                  var i = 0
                                  while i < n {
                                      i += 1
                                      total += i
                                  }
                                  return total
                              }
                              @main
                              fn main() { print(sum(1000)) }
                              """;

        Assert.Equal("500500\n", BuildAndRun(source, optimise: false).Stdout);
        Assert.Equal("500500\n", BuildAndRun(source).Stdout);
    }

    [Fact]
    public void HandWrittenSilWithBlockArgumentsRunsNatively() {
        if (!CanBuild) {
            return;
        }

        // cond_br passing arguments needs edge blocks so that each phi has
        // one value per predecessor; nothing the lowering emits does that yet.
        var module = SilParser.Parse(
            """
            sil @sumTo : $(Int) -> Int {
            bb0(%0 : $Int):
              %1 = integer_literal $Int, 0
              br bb1(%1, %1)
            bb1(%2 : $Int, %3 : $Int):
              %4 = builtin "cmp_lt_Int"(%2, %0) : $Bool
              cond_br %4, bb2(%2, %3), bb3(%3)
            bb2(%5 : $Int, %6 : $Int):
              %7 = integer_literal $Int, 1
              %8 = builtin "add_Int"(%5, %7) : $Int
              %9 = builtin "add_Int"(%6, %8) : $Int
              br bb1(%8, %9)
            bb3(%10 : $Int):
              return %10
            }

            sil @main : $() -> () {
            bb0:
              %0 = function_ref @sumTo : $(Int) -> Int
              %1 = integer_literal $Int, 100
              %2 = apply %0(%1) : $Int
              builtin "print_Int"(%2) : $()
              return
            }
            """
        );
        Assert.Empty(SilVerifier.Verify(module));
        module.EntryPoint = "main";

        var interpreter = new SilInterpreter(module);
        interpreter.Run("main");
        Assert.Equal("5050\n", interpreter.Output.ToString());

        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(LlvmEmitter.Emit(module), executable);
            Assert.True(build.Succeeded, build.Output);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true }.Utf8();
            using var process = Process.Start(start)!;
            Assert.Equal("5050\n", process.StandardOutput.ReadToEnd());
            process.WaitForExit();
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }

    [Fact]
    public void AHelloWorldBinaryIsSmall() {
        if (!CanBuild) {
            return;
        }

        // docs/04 §2.5: under 400 KB stripped.
        var result = Pipeline.Compile(new SourceText("module H\n@main\nfn main() { print(\"hi\") }\n", "h.sb"));
        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            Assert.True(NativeBuild.Build(result.LlvmIr!, executable).Succeeded);
            Assert.True(new FileInfo(executable).Length < 400 * 1024, $"{new FileInfo(executable).Length:N0} bytes");
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }

    /// <summary>
    ///     A trap names the line and column it happened at — one the compiled
    ///     code checks itself, and one the runtime checks for a call — and the
    ///     interpreter names the same place (#187).
    /// </summary>
    [Theory]
    [InlineData("    let values = [1, 2]\n    print(values[2])", "array index out of range", "test.sb:5:11")]
    [InlineData("    let text = \"ab\"\n    let c = text.at(7)", "string index out of range", "test.sb:5:13")]
    [InlineData("    let n = 9223372036854775807\n    let m = 2\n    print(m + n)", "integer overflow", "test.sb:6:11")]
    [InlineData("    let o: Int? = none\n    print(o!)", "unwrapped a none", "test.sb:5:11")]
    public void ATrapSaysWhereItHappened(string body, string reason, string site) {
        var run = Programs.AssertAgree($"module Traps\n@main\nfn main() {{\n{body}\n}}\n");
        Assert.Equal($"fatal error: {reason}\n  at {site}\n", run.Stderr);
    }

    /// <summary>
    ///     Recursion that runs off the end of the stack is a trap with a
    ///     message, as the interpreter's is — not a bare SIGSEGV — on the
    ///     program's own thread and on a task's (#187).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunningOutOfStackIsATrap(bool inATask) {
        const string recursion = """
                                 module Deep
                                 fn down(_ n: Int) -> Int {
                                     var kept = [n, n]
                                     if n == 0 {
                                         return 0
                                     }
                                     let below = down(n - 1)
                                     return below + kept[1] - kept[0]
                                 }
                                 """;
        var body = inATask
            ? "@main\nasync fn main() {\n    print(\"before\")\n    let t = Task.spawn { down(100000000) }\n    print(await t.value)\n}\n"
            : "@main\nfn main() {\n    print(\"before\")\n    print(down(100000000))\n}\n";
        var run = Programs.AssertAgree($"{recursion}\n{body}");
        Assert.Equal("before\n", run.Stdout);
        Assert.Equal("fatal error: stack overflow: calls nested too deeply\n", run.Stderr);
        if (CanBuild) {
            Assert.Equal(134, run.ExitCode);
        }
    }

    /// <summary>With SABLE_BACKTRACE set, a trap says where it was; without it, only what happened.</summary>
    [Fact]
    public void ATrapCanSayWhereItWas() {
        if (!CanBuild) {
            return;
        }

        const string source = """
                              module P
                              fn at(_ values: Int[>..], _ i: Int) -> Int { return values[i] }
                              @main
                              fn main() {
                                  print(at([1, 2], 5))
                              }
                              """;
        var traced = Programs.BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "1" });
        Assert.NotEqual(0, traced.ExitCode);
        Assert.Contains("fatal error: ", traced.Stderr, StringComparison.Ordinal);
        Assert.Contains("\nbacktrace:\n", traced.Stderr, StringComparison.Ordinal);
        Assert.Contains("main", traced.Stderr, StringComparison.Ordinal);

        var plain = Programs.BuildAndRun(source);
        Assert.DoesNotContain("backtrace", plain.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An unoptimised build's trap names every frame, not just the innermost:
    ///     the functions keep the frame-pointer chain the backtrace walks.
    /// </summary>
    [Fact]
    public void AnUnoptimisedTrapNamesEveryFrame() {
        if (!CanBuild) {
            return;
        }

        const string source = """
                              module P
                              fn inner(_ values: Int[>..]) -> Int { return values[5] }
                              fn outer() -> Int { return inner([1, 2]) }
                              @main
                              fn main() {
                                  print(outer())
                              }
                              """;
        var traced = Programs.BuildAndRun(source, optimise: false, environment: new Dictionary<string, string> { ["SABLE_BACKTRACE"] = "1" });
        Assert.NotEqual(0, traced.ExitCode);
        var inner = traced.Stderr.IndexOf("P.inner", StringComparison.Ordinal);
        var outer = traced.Stderr.IndexOf("P.outer", StringComparison.Ordinal);
        Assert.True(inner >= 0 && outer > inner, traced.Stderr);
    }

    /// <summary>
    ///     Every function the program defines carries the host's attributes, so
    ///     that LTO may inline the runtime into it; quoted names and debug
    ///     metadata are kept as they were.
    /// </summary>
    [Fact]
    public void DefinitionsTakeTheHostsAttributes() {
        const string ir = """
                          define void @"sable.P.f(x)"(i64 %a) !dbg !5 {
                          define internal { i64, ptr } @g(ptr %p) {
                          declare void @sable_retain(ptr)
                          """;
        var marked = NativeBuild.WithHostAttributes(ir, "\"target-cpu\"=\"x\"");
        Assert.Contains("define void @\"sable.P.f(x)\"(i64 %a) #9000 !dbg !5 {", marked, StringComparison.Ordinal);
        Assert.Contains("define internal { i64, ptr } @g(ptr %p) #9000 {", marked, StringComparison.Ordinal);
        Assert.Contains("declare void @sable_retain(ptr)\n", marked, StringComparison.Ordinal);
        Assert.EndsWith("attributes #9000 = { \"target-cpu\"=\"x\" }\n", marked, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The runtime is compiled once for each set of options that changes
    ///     it, and kept: a second program built the same way links the object
    ///     the first one left, rather than compiling 3,000 lines of C again.
    ///     A build with other options makes, and keeps, another.
    /// </summary>
    [Fact]
    public void TheRuntimeIsCompiledOnceAndKept() {
        if (!CanBuild) {
            return;
        }

        var cache = Directory.CreateTempSubdirectory("sable-runtime-cache-");
        var executable = Platform.Executable(Path.Combine(cache.FullName, "program"));
        try {
            var compiled = Pipeline.Compile(new SourceText("module Kept\n\n@main\nfn main() {\n    print(\"kept\")\n}\n", "test.sb"));
            Assert.True(compiled.Succeeded, string.Join("\n", compiled.Errors));
            var ir = compiled.LlvmIr!;
            var options = NativeOptions.Of(optimise: true, debug: false);
            Assert.True(NativeBuild.Build(ir, executable, options, cache.FullName).Succeeded);
            var runtime = Assert.Single(Directory.GetFiles(cache.FullName, "runtime-*.o"));
            var written = File.GetLastWriteTimeUtc(runtime);

            Platform.DeleteExecutable(executable);
            Assert.True(NativeBuild.Build(ir, executable, options, cache.FullName).Succeeded);
            Assert.Equal(written, File.GetLastWriteTimeUtc(Assert.Single(Directory.GetFiles(cache.FullName, "runtime-*.o"))));
            Assert.Equal(("kept\n", 0), PackageFixture.Execute(executable));

            // Unoptimised, it is another object.
            Assert.True(NativeBuild.Build(ir, executable, NativeOptions.Of(optimise: false, debug: false), cache.FullName).Succeeded);
            Assert.Equal(2, Directory.GetFiles(cache.FullName, "runtime-*.o").Length);
            Assert.Equal(("kept\n", 0), PackageFixture.Execute(executable));
        } finally {
            cache.Delete(true);
        }
    }
}
