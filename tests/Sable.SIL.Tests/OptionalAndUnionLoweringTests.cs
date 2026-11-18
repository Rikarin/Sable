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

    // ---- Unions (#146) ------------------------------------------------------

    [Fact]
    public void AnIfThatLeavesNarrowsToWhatIsLeft() {
        var module = Lower(
            """
            struct Vec3 { var x: Int, var y: Int }
            fn render(_ v: Int | String | Vec3) -> Int {
                if v is Int { return v }
                if v is String { return v.count }
                return v.x + v.y
            }
            fn rest(_ v: Int | String | Vec3) -> Int {
                if v is Int { return 0 }
                let w: String | Vec3 = v
                if w is String { return w.count * 10 }
                return 2
            }
            fn a() -> Int { return render(7) }
            fn b() -> Int { return render("four") }
            fn c() -> Int { return render(Vec3 { x: 1, y: 2 }) }
            fn d() -> Int { return rest("abc") }
            fn e() -> Int { return rest(Vec3 { x: 1, y: 2 }) }
            fn f() -> Int { return rest(5) }
            fn last(_ v: F64 | String) -> Int {
                if v is F64 { return 1 }
                if v is String { return v.count }
                return 3
            }
            fn g() -> Int { return last("hello") }
            """
        );

        Assert.Equal(5L, Run(module, "g"));

        Assert.Equal(7L, Run(module, "a"));
        Assert.Equal(4L, Run(module, "b"));
        Assert.Equal(3L, Run(module, "c"));
        Assert.Equal(30L, Run(module, "d"));
        Assert.Equal(2L, Run(module, "e"));
        Assert.Equal(0L, Run(module, "f"));
    }

    [Fact]
    public void NoneInAUnionIsItsAbsence() {
        var module = Lower(
            """
            fn f(_ x: Int | String | None) -> Int {
                guard let y = x else { return 0 }
                if y is Int { return y }
                return -1
            }
            fn a() -> Int { return f(none) }
            fn b() -> Int { return f(4) }
            fn c() -> Int { return f("s") }
            """
        );

        Assert.Equal(0L, Run(module, "a"));
        Assert.Equal(4L, Run(module, "b"));
        Assert.Equal(-1L, Run(module, "c"));
    }

    [Fact]
    public void ARecursiveUnionHoldsItself() {
        var module = Lower(
            """
            alias Json = Bool | F64 | String | Json[] | [String: Json]
            fn leaves(_ j: Json) -> Int {
                if j is Json[] {
                    var n = 0
                    for x in j { n = n + leaves(x) }
                    return n
                }
                if j is [String: Json] {
                    var n = 0
                    for (k, v) in j { n = n + leaves(v) }
                    return n
                }
                return 1
            }
            fn shape(_ j: Json) -> Int {
                return match j {
                    case let b as Bool => 1
                    case let f as F64 => 2
                    case let s as String => 3
                    case let xs as Json[] => 100 + xs.count
                    case let d as [String: Json] => 200 + d.count
                }
            }
            fn count() -> Int {
                let inner: Json = [1.5, "x", false]
                let object: Json = ["a": inner, "b": "y"]
                let all: Json = [object, inner, true]
                return leaves(all)
            }
            fn outer() -> Int {
                let object: Json = ["a": true, "b": "y"]
                return shape(object)
            }
            """
        );

        Assert.Equal(8L, Run(module, "count"));
        Assert.Equal(202L, Run(module, "outer"));
    }
}
