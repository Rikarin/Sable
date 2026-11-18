namespace Sable.SIL.Tests;

/// <summary>
///     Compile-time evaluation (SYNTAX.md §21): each <c>const</c> computed and
///     each <c>comptime</c> block run while the program is lowered, in the
///     interpreter; the constants then written where they are read.
/// </summary>
public sealed class CompileTimeLoweringTests {
    static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) LowerWithDiagnostics(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        return SilGen.Lower(compilation);
    }

    static SilModule Lower(string source) {
        var (module, diagnostics) = LowerWithDiagnostics(source);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));
        var problems = SilVerifier.Verify(module);
        Assert.True(problems.Count == 0, string.Join("\n", problems) + "\n\n" + SilPrinter.Print(module));
        return module;
    }

    static Diagnostic Reports(ErrorCode code, string source) {
        var (_, diagnostics) = LowerWithDiagnostics(source);
        Assert.True(diagnostics.Count == 1 && diagnostics[0].Code == code, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));
        return diagnostics[0];
    }

    static object? Run(SilModule module, string function) => new SilInterpreter(module).Run($"M.{function}");

    static bool Calls(SilModule module, string caller, string callee) =>
        module.Find($"M.{caller}")!.Blocks.SelectMany(static b => b.Instructions)
            .Any(i => i is FunctionRefInst reference && reference.Function == $"M.{callee}");

    const string Factorial = "const fn factorial(_ n: Int) -> Int => n <= 1 ? 1 : n * factorial(n - 1)\n";

    [Fact]
    public void AConstantIsComputedWhileCompilingAndWrittenWhereItIsRead() {
        var module = Lower(Factorial + "const Table = factorial(5)\nfn read() -> Int => Table");
        Assert.Equal(120L, Run(module, "read"));

        // Nothing is left to do when the program runs: no storage, no call.
        Assert.Empty(module.Globals);
        Assert.False(Calls(module, "read", "factorial"));
        Assert.Null(module.Find("$globals.init"));
    }

    [Fact]
    public void AConstantMayReadAnotherDeclaredAfterIt() {
        var module = Lower(Factorial + "const Twice: Int = Table * 2\nconst Table: Int = factorial(4)\nfn read() -> Int => Twice");
        Assert.Equal(48L, Run(module, "read"));
    }

    [Fact]
    public void StringsFloatsAndStructsAreWrittenAsLiterals() {
        var module = Lower(
            """
            struct Point {
                x: Int
                y: F64
            }
            const fn greeting(_ name: String) -> String => "hello, " + name
            const fn point() -> Point => Point(x: 3, y: 0.5)
            const Greeting = greeting("sable")
            const Origin = point()
            fn text() -> String => Greeting
            fn sum() -> F64 => Origin.y + 1.0
            """
        );

        Assert.Equal("hello, sable", Run(module, "text"));
        Assert.Equal(1.5, Run(module, "sum"));
        Assert.Empty(module.Globals);
    }

    [Fact]
    public void ALiteralConstantIsInlined() {
        var module = Lower("const Pi = 3.14\nfn read() -> F64 => Pi");
        Assert.Equal(3.14, Run(module, "read"));
        Assert.Empty(module.Globals);
    }

    [Fact]
    public void AConstantNoLiteralCanSayIsKeptInAGlobal() {
        var module = Lower("const fn table() -> Int[] => [1, 2, 3]\nconst Table = table()\nfn second() -> Int => Table[1]");
        Assert.Single(module.Globals);
    }

    [Fact]
    public void TheConstFnIsStillAnOrdinaryFunctionAtRunTime() {
        var module = Lower(Factorial + "fn read(_ n: Int) -> Int => factorial(n)");
        Assert.Equal(24L, new SilInterpreter(module).Run("M.read", 4L));
    }

    [Fact]
    public void ThePlatformsConstantsAreKnown() {
        var module = Lower("fn width() -> Int => Platform.pointerWidth\nfn os() -> String => Platform.os");
        Assert.Equal(64L, Run(module, "width"));
        // The machine compiling: what a program built without --target runs on.
        Assert.Equal(ParseOptions.HostOs, Run(module, "os"));
    }

    /// <summary>
    ///     A program built for another machine sees that machine's constants:
    ///     the core library is parsed for the target its files are, so its
    ///     '#if os(...)' follows the program's (it once said macOS everywhere).
    /// </summary>
    [Theory]
    [InlineData("linux", "x86_64", 64L)]
    [InlineData("macos", "arm64", 64L)]
    [InlineData("wasi", "wasm32", 32L)]
    public void ThePlatformsConstantsAreTheTargets(string os, string arch, long width) {
        var options = ParseOptions.Default.ForTarget(os, arch);
        var tree = SyntaxTree.Parse(new SourceText("module M\nfn width() -> Int => Platform.pointerWidth\nfn os() -> String => Platform.os\nfn arch() -> String => Platform.arch", "m.sb"), options);
        var (module, diagnostics) = SilGen.Lower(Compilation.Create([tree]));
        Assert.Empty(diagnostics);
        Assert.Equal(os, Run(module, "os"));
        Assert.Equal(arch, Run(module, "arch"));
        Assert.Equal(width, Run(module, "width"));
    }

    // ---- What goes wrong ---------------------------------------------------

    [Fact]
    public void DivisionByZeroIsAnErrorAtTheConstant() {
        var error = Reports(ErrorCode.ERR_ConstantOverflow, "const fn zero() -> Int => 0\nconst E: Int = 1 / zero()");
        Assert.Contains("division by zero", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OverflowInAConstFnIsAnErrorAtTheConstant() =>
        Assert.Contains("overflow", Reports(ErrorCode.ERR_ConstantOverflow,
                "const fn next(_ n: Int) -> Int => n + 1\nconst Big: Int = next(9223372036854775807)").Message,
            StringComparison.Ordinal);

    [Fact]
    public void AConstantDefinedInTermsOfItselfIsOneError() {
        var error = Reports(ErrorCode.ERR_ConstantEvaluationFailed, "const A: Int = B + 1\nconst B: Int = A + 1");
        Assert.Contains("in terms of itself", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoopThatNeverEndsStopsAtTheStepLimit() =>
        Assert.Contains("steps", Reports(ErrorCode.ERR_ConstantEvaluationFailed,
            """
            const fn spin() -> Int {
                var i = 0
                while true {
                    i += 1
                }
                return i
            }
            const L: Int = spin()
            """).Message, StringComparison.Ordinal);

    // ---- comptime ----------------------------------------------------------

    [Fact]
    public void ACompileErrorAComptimeBlockReachesIsReportedAtTheCall() {
        const string source = """
                              comptime {
                                  if Platform.pointerWidth == 64 {
                                      compileError("no 64-bit please")
                                  }
                              }
                              """;
        var error = Reports(ErrorCode.ERR_CompileError, source);
        Assert.Equal("no 64-bit please", error.Message);

        // The span is the call's, in the module's own text.
        var text = $"module M\n{source}";
        Assert.StartsWith("compileError(", text[error.Span.Start..], StringComparison.Ordinal);
    }

    [Fact]
    public void AComptimeBlockThatPassesLeavesNoCode() {
        var module = Lower(
            Factorial + """
                        fn f() -> Int {
                            comptime {
                                if factorial(3) != 6 {
                                    compileError("arithmetic is broken")
                                }
                            }
                            return 1
                        }
                        """
        );

        Assert.False(Calls(module, "f", "factorial"));
        Assert.Equal(1L, Run(module, "f"));
    }

    [Fact]
    public void AComptimeBlockInABodyReportsItsCompileError() =>
        Assert.Equal("broken", Reports(ErrorCode.ERR_CompileError, "fn f() {\n    comptime {\n        compileError(\"broken\")\n    }\n}").Message);

    [Fact]
    public void ACompileErrorReachedComputingAConstantIsReported() =>
        Assert.Equal("negative", Reports(ErrorCode.ERR_CompileError,
            """
            const fn checked(_ n: Int) -> Int {
                if n < 0 {
                    compileError("negative")
                }
                return n
            }
            const N: Int = checked(-1)
            """).Message);

    [Fact]
    public void CompileErrorReachedAtRunTimeStopsTheProgram() {
        var module = Lower(
            """
            const fn checked(_ n: Int) -> Int {
                if n < 0 {
                    compileError("negative")
                }
                return n
            }
            fn read(_ n: Int) -> Int => checked(n)
            """
        );
        Assert.Equal(3L, new SilInterpreter(module).Run("M.read", 3L));
        var trap = Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.read", -1L));
        Assert.Contains("negative", trap.Message, StringComparison.Ordinal);
    }
}
