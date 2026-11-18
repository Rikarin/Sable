namespace Sable.SIL.Tests;

/// <summary>
///     Optionals and structural unions as programs compute them: promotion
///     that takes a step first, unions that normalise, chains through
///     optional members, and the operators that compare them.
/// </summary>
public sealed class OptionalAndUnionLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));

        var problems = SilVerifier.Verify(module);
        Assert.True(problems.Count == 0, string.Join("\n", problems) + "\n\n" + SilPrinter.Print(module));

        var text = SilPrinter.Print(module);
        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));

        SilPassManager.Run(reparsed, SilPassManager.Default);
        return reparsed;
    }

    static object? Run(SilModule module, string function, params object[] arguments) {
        var interpreter = new SilInterpreter(module);
        var result = interpreter.Run($"M.{function}", arguments);
        Assert.True(interpreter.LiveObjects == 0, $"{interpreter.LiveObjects} object(s) leaked");
        return result;
    }

    // ---- Promotion that takes a step first (#145) ---------------------------

    [Fact]
    public void AnArrayLiteralTakesItsKindThenWraps() {
        var module = Lower(
            """
            fn count() -> Int {
                let xs: Int[>..]? = [9, 8]
                if let ys = xs { return ys.count }
                return -1
            }
            """
        );

        Assert.Equal(2L, Run(module, "count"));
    }

    [Fact]
    public void AValueJoinsTheUnionThenWraps() {
        var module = Lower(
            """
            fn which(_ pick: Bool) -> Int {
                let u: (Int | String)? = pick ? none : "x"
                if let v = u {
                    if v is String { return 2 }
                    return 1
                }
                return 0
            }
            fn passed() -> Int { return take("x") }
            fn take(_ u: (Int | String)?) -> Int {
                guard let v = u else { return 0 }
                return v is String ? 2 : 1
            }
            """
        );

        Assert.Equal(2L, Run(module, "which", false));
        Assert.Equal(0L, Run(module, "which", true));
        Assert.Equal(2L, Run(module, "passed"));
    }

    [Fact]
    public void APlainAsGivesItsOperandTheType() {
        var module = Lower(
            """
            fn nothing() -> Int { return (none as Int?) ?? 7 }
            fn some() -> Int { return (3 as Int?) ?? 7 }
            fn array() -> Int { return ([1, 2] as Int[>..]?)?.count ?? 0 }
            """
        );

        Assert.Equal(7L, Run(module, "nothing"));
        Assert.Equal(3L, Run(module, "some"));
        Assert.Equal(2L, Run(module, "array"));
    }
}
