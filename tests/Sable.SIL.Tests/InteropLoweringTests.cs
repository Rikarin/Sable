namespace Sable.SIL.Tests;

/// <summary>
///     <c>@extern</c> and <c>@export</c> in SIL: a C function is declared by
///     the module (<c>sil_extern</c>) and called with an ordinary
///     <c>apply</c>; an exported one is named for C (<c>sil_export</c>) and
///     kept, however little the program calls it.
/// </summary>
public sealed class InteropLoweringTests {
    const string Source = """
                          @extern("c", "abs")
                          fn cAbs(_ x: I32) -> I32

                          @export("c", "sable_twice")
                          fn twice(_ x: I32) -> I32 => x * 2

                          fn main() {
                              print(cAbs(-5))
                          }
                          """;

    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.Empty(diagnostics);
        Assert.Empty(SilVerifier.Verify(module));
        return module;
    }

    [Fact]
    public void AnExternFunctionIsDeclaredAndCalled() {
        var module = Lower(Source);
        Assert.Equal(("abs", "(I32) -> I32"), (module.Foreign["M.cAbs"].Symbol, module.Foreign["M.cAbs"].Type.ToString()));
        Assert.Null(module.Find("M.cAbs"));
        Assert.Contains(module.Find("M.main")!.Blocks.SelectMany(static b => b.Instructions),
            static i => i is FunctionRefInst { Function: "M.cAbs" });
    }

    [Fact]
    public void AnExportedFunctionIsNamedForC() {
        var module = Lower(Source);
        Assert.Equal("sable_twice", module.Exports["M.twice"]);
        Assert.NotNull(module.Find("M.twice"));
    }

    [Fact]
    public void AnExportedFunctionIsKeptThoughNothingCallsIt() {
        var module = Lower(Source);
        SilPassManager.RemoveUnreachable(module);
        Assert.NotNull(module.Find("M.twice"));
    }

    [Fact]
    public void BothRoundTripThroughText() {
        var module = Lower(Source);
        var text = SilPrinter.Print(module);
        Assert.Contains("sil_extern @M.cAbs : $(I32) -> I32 = \"abs\"", text, StringComparison.Ordinal);
        Assert.Contains("sil_export @M.twice = \"sable_twice\"", text, StringComparison.Ordinal);

        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));
        Assert.Empty(SilVerifier.Verify(reparsed));
    }

    /// <summary>
    ///     A variadic C function is declared for each call's types after the
    ///     fixed ones — the values themselves, not CVarArg — and says how
    ///     many are fixed; an exported variable's storage is named for C.
    /// </summary>
    [Fact]
    public void AVariadicCallAndAnExportedVariableRoundTripThroughText() {
        var module = Lower("""
                           @extern("c")
                           fn add(_ n: I32, _ values: CVarArg...) -> Int

                           @export("sable_count")
                           var count: Int = 0

                           fn main() {
                               let small: I8 = 1
                               count = add(2, 10, small)
                           }
                           """);
        var text = SilPrinter.Print(module);
        Assert.Contains("sil_extern @M.add$va$Int$I8 : $(I32, Int, I8) -> Int = \"add\" variadic 1", text, StringComparison.Ordinal);
        Assert.Contains("sil_export @M.count = \"sable_count\"", text, StringComparison.Ordinal);

        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));
        Assert.Empty(SilVerifier.Verify(reparsed));
    }

    [Fact]
    public void TheInterpreterSaysItCannotCallC() {
        var module = Lower(Source);
        var trap = Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run(module.EntryPoint!));
        Assert.Contains("'abs' is a C function", trap.Message, StringComparison.Ordinal);
    }
}
