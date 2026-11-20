namespace Sable.SIL.Tests;

/// <summary>
///     Untyped throws, union throws, rethrows and function conversions that
///     add a throw: lowered, verified before and after the passes, printed and
///     parsed back, and run.
/// </summary>
public sealed class ErrorLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));
        Assert.True(SilVerifier.Verify(module).Count == 0, string.Join("\n", SilVerifier.Verify(module)) + "\n\n" + SilPrinter.Print(module));

        var text = SilPrinter.Print(module);
        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));
        Assert.Empty(SilVerifier.Verify(reparsed));

        SilPassManager.Run(reparsed, SilPassManager.Default);
        Assert.True(SilVerifier.Verify(reparsed).Count == 0, string.Join("\n", SilVerifier.Verify(reparsed)));
        return reparsed;
    }

    static object? Run(SilModule module, string function, params object[] arguments) {
        var interpreter = new SilInterpreter(module);
        var result = interpreter.Run($"M.{function}", arguments);
        Assert.Equal(0, interpreter.LiveObjects);
        return result;
    }

    const string Errors = """
                          union IoError { NotFound(path: Int), Denied }
                          union NetError { Down(code: Int) }

                          fn read(_ n: Int) throws(IoError) -> Int {
                              if n == 0 { throw IoError.Denied }
                              if n < 0 { throw IoError.NotFound(path: n) }
                              return n
                          }

                          fn fetch(_ n: Int) throws(NetError) -> Int {
                              if n > 100 { throw NetError.Down(code: n) }
                              return n
                          }
                          """;

    [Fact]
    public void AnErrorNoCatchMatchesLeavesTheDo() {
        var module = Lower(
            Errors + """

            fn partial(_ n: Int) throws(IoError) -> Int {
                do {
                    return try read(n)
                } catch .Denied {
                    return -1
                }
            }

            fn outer(_ n: Int) -> Int {
                do {
                    return try partial(n)
                } catch .NotFound(let p) {
                    return p * 100
                } catch .Denied {
                    return -2
                }
            }
            """
        );

        Assert.Equal(5L, Run(module, "outer", 5L));
        Assert.Equal(-1L, Run(module, "outer", 0L));
        Assert.Equal(-300L, Run(module, "outer", -3L));
    }

    [Fact]
    public void BareThrowsBoxesTheErrorAndACatchUnboxesIt() {
        var module = Lower(
            Errors + """

            fn bare(_ n: Int) throws -> Int {
                return try read(n) + fetch(n)
            }

            fn classify(_ n: Int) -> Int {
                do {
                    return try bare(n)
                } catch let e as IoError {
                    match e {
                        case .NotFound(let p): return p * 10
                        case .Denied: return -1
                    }
                } catch let e as NetError {
                    match e {
                        case .Down(let code): return code + 1
                    }
                } catch {
                    return -99
                }
            }
            """
        );

        Assert.Equal(10L, Run(module, "classify", 5L));
        Assert.Equal(-1L, Run(module, "classify", 0L));
        Assert.Equal(-30L, Run(module, "classify", -3L));
        Assert.Equal(201L, Run(module, "classify", 200L));
    }

    [Fact]
    public void AUnionErrorCarriesWhicheverWasThrown() {
        var module = Lower(
            Errors + """

            fn both(_ n: Int) throws(IoError | NetError) -> Int {
                return try read(n) + fetch(n)
            }

            fn classify(_ n: Int) -> Int {
                do {
                    return try both(n)
                } catch let e as NetError {
                    return 1000
                } catch {
                    return -1
                }
            }
            """
        );

        Assert.Equal(14L, Run(module, "classify", 7L));
        Assert.Equal(-1L, Run(module, "classify", 0L));
        Assert.Equal(1000L, Run(module, "classify", 500L));
    }

    [Fact]
    public void RethrowsCallsNeedNoHandlerWhenTheirArgumentCannotThrow() {
        var module = Lower(
            Errors + """

            fn twice(_ body: () throws(IoError) -> Int) rethrows -> Int {
                return try body() + body()
            }

            fn quiet() -> Int {
                return twice(() => 21)
            }

            fn loud(_ n: Int) -> Int {
                return try twice(() => { return try read(n) }) catch { -1 }
            }
            """
        );

        Assert.Equal(42L, Run(module, "quiet"));
        Assert.Equal(6L, Run(module, "loud", 3L));
        Assert.Equal(-1L, Run(module, "loud", 0L));
    }

    [Fact]
    public void AnInlineCatchBlockRunsEveryStatementBeforeItsValue() {
        // The block's statements run, its locals are released and its defer
        // runs after the value is computed: 15, with 'steps' at 11.
        var module = Lower(
            Errors + """

            class Box {
                var n: Int
                init(_ n: Int) { self.n = n }
            }

            fn recover(_ n: Int) -> Int {
                var steps = 0
                let v = try read(n) catch {
                    steps += 10
                    let b = Box(steps)
                    defer { steps += 1 }
                    b.n + 5
                }
                return v * 100 + steps
            }
            """
        );

        Assert.Equal(300L, Run(module, "recover", 3L));
        Assert.Equal(1511L, Run(module, "recover", 0L));
    }

    [Fact]
    public void AFunctionIsConvertedToOneThatThrowsMore() {
        var module = Lower(
            Errors + """

            fn apply(_ f: (Int) throws -> Int, _ x: Int) throws -> Int {
                return try f(x)
            }

            fn plain(_ x: Int) -> Int {
                return try! apply((y: Int) => y * 2, x)
            }

            fn typed(_ x: Int) -> Int {
                do {
                    return try apply((y: Int) => { return try read(y) }, x)
                } catch let e as IoError {
                    return -2
                } catch {
                    return -3
                }
            }
            """
        );

        Assert.Equal(8L, Run(module, "plain", 4L));
        Assert.Equal(9L, Run(module, "typed", 9L));
        Assert.Equal(-2L, Run(module, "typed", 0L));
    }

    [Fact]
    public void EnumsCompareByCase() {
        var module = Lower(
            """
            enum Kind { Number, Plus, End }

            fn same(_ a: Int, _ b: Int) -> Bool {
                let kinds = [Kind.Number, Kind.Plus, Kind.End]
                return kinds[a] == kinds[b]
            }

            fn rem(_ a: F64, _ b: F64) -> F64 { return a % b }
            """
        );

        Assert.Equal(true, Run(module, "same", 1L, 1L));
        Assert.Equal(false, Run(module, "same", 0L, 2L));
        Assert.Equal(1.5, Run(module, "rem", 7.5, 2.0));
        Assert.Equal(-1.5, Run(module, "rem", -7.5, 2.0));
    }

    // ---- A closure that says what it throws (#273) --------------------------

    [Fact]
    public void AClosureThrowsTheErrorItsHeaderNames() {
        var module = Lower(
            """
            union ParseError: Error { Empty }

            fn apply<E: Error>(_ s: String, _ f: (String) throws(E) -> Int) throws(E) -> Int {
                return try f(s)
            }

            fn measure(_ s: String) -> Int {
                let parse = { (s: String) throws(ParseError) -> Int in
                    if s.isEmpty { throw ParseError.Empty }
                    return s.count
                }
                let arrow = (s: String) throws(ParseError) -> Int => try parse(s) * 10
                do {
                    return try apply(s, parse) + arrow(s)
                } catch {
                    return -1
                }
            }
            """
        );

        Assert.Equal(33L, Run(module, "measure", "abc"));
        Assert.Equal(-1L, Run(module, "measure", ""));
    }
}
