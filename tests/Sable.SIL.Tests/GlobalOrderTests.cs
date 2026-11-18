namespace Sable.SIL.Tests;

/// <summary>
///     Globals and statics are given their initial values each after those it
///     reads, whatever order they are declared in and whichever file; ones
///     that need each other are an error (S3411). They used to be set in
///     declaration order, and one reading a later one read zero.
/// </summary>
public sealed class GlobalOrderTests {
    static Compilation Program(params string[] files) =>
        Compilation.Create([..files.Select(static (text, i) => SyntaxTree.Parse(new SourceText($"module M\n{text}", $"f{i}.sb")))]);

    static string Run(params string[] files) {
        var compilation = Program(files);
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));
        var problems = SilVerifier.Verify(module);
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var interpreter = new SilInterpreter(module);
        interpreter.Run("M.main");
        return interpreter.Output.ToString();
    }

    static List<Diagnostic> Lowering(params string[] files) {
        var compilation = Program(files);
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        return [..SilGen.Lower(compilation).Diagnostics];
    }

    [Fact]
    public void AGlobalReadingALaterOneIsSetAfterIt() =>
        Assert.Equal("44\n", Run("let total: Int = base * 2 + 2\nlet base: Int = 21\nfn main() { print(total) }"));

    [Fact]
    public void WhatAFunctionReadsIsSetBeforeTheGlobalThatCallsIt() =>
        Assert.Equal(
            "3\n2\n",
            Run(
                """
                let total: Int = offset() + 1
                fn offset() -> Int { return Config.extra }
                struct Config {
                    static let extra: Int = Config.seed + 1
                    static let seed: Int = 1
                }
                fn main() {
                    print(total)
                    print(Config.extra)
                }
                """
            )
        );

    [Fact]
    public void AnObjectsFieldsInitialValuesCountAsItsInitialisers() =>
        Assert.Equal(
            "hello\n",
            Run(
                """
                class Greeter { var text: String = greeting }
                let greeter: Greeter = Greeter()
                let greeting: String = "hello"
                fn main() { print(greeter.text) }
                """
            )
        );

    [Fact]
    public void TheOrderHoldsAcrossFiles() =>
        Assert.Equal(
            "10\n",
            Run(
                "let doubled: Int = Limits.most * 2\nfn main() { print(doubled) }",
                "struct Limits { static let most: Int = five() }\nfn five() -> Int { return 5 }"
            )
        );

    [Fact]
    public void GlobalsThatNeedNothingOfEachOtherKeepTheirOrder() =>
        Assert.Equal(
            "a\nb\nc\n",
            Run(
                """
                let a: Int = say("a")
                let b: Int = say("b")
                let c: Int = say("c")
                fn say(_ s: String) -> Int {
                    print(s)
                    return 0
                }
                fn main() { }
                """
            )
        );

    [Fact]
    public void TwoGlobalsThatReadEachOtherAreAnError() {
        var diagnostic = Assert.Single(Lowering("let a: Int = b + 1\nlet b: Int = twice()\nfn twice() -> Int { return a * 2 }\nfn main() { print(a) }"));
        Assert.Equal(ErrorCode.ERR_GlobalInitializationCycle, diagnostic.Code);
        Assert.Equal(
            "the initial values of 'a' and 'b' need each other, so none can be set first: 'a' reads 'b' and 'b' reads 'a' through 'twice()'",
            diagnostic.Message
        );
    }

    [Fact]
    public void AStaticThatNeedsItselfIsAnError() {
        var diagnostic = Assert.Single(Lowering("struct S { static let n: Int = again() }\nfn again() -> Int { return S.n }\nfn main() { print(S.n) }"));
        Assert.Equal(ErrorCode.ERR_GlobalInitializationCycle, diagnostic.Code);
        Assert.Equal("the initial value of 'S.n' needs itself: 'S.n' reads 'S.n' through 'again()'", diagnostic.Message);
    }

    [Fact]
    public void ACycleAcrossFilesIsAnError() {
        var diagnostic = Assert.Single(Lowering("let a: Int = b\nfn main() { print(a) }", "let b: Int = a"));
        Assert.Equal(ErrorCode.ERR_GlobalInitializationCycle, diagnostic.Code);
    }
}
