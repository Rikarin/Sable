namespace Sable.SIL.Tests;

/// <summary>
///     The operators SYNTAX.md §13 lets a type declare or derive, and the
///     comparisons of §16, as programs compute them: <c>operator []</c> and
///     <c>operator ()</c>, compound assignment from a binary operator,
///     <c>===</c>, and <c>==</c> on tuples and optionals.
/// </summary>
public sealed class OperatorLoweringTests {
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

    const string Money = """
                         struct Money {
                             var cents: Int
                             operator + (a: Money, b: Money) -> Money { return Money(cents: a.cents + b.cents) }
                             operator - (a: Money, b: Money) -> Money { return Money(cents: a.cents - b.cents) }
                             operator == (a: Money, b: Money) -> Bool { return a.cents == b.cents }
                             operator [] (m: Money, k: String) -> Int { return m.cents + k.count }
                             operator () (m: Money, times: Int) -> Int { return m.cents * times }
                         }
                         class Box {
                             var v: Int
                             init(v: Int) { self.v = v }
                         }
                         class Crate: Box {
                             init() { super.init(v: 0) }
                         }
                         """;

    [Fact]
    public void ATypesOwnIndexingAndCallingOperators() {
        var module = Lower(
            Money + """

                    fn indexed() -> Int { return Money(cents: 5)["abc"] }
                    fn called() -> Int { return Money(cents: 5)(3) }
                    fn labelled() -> Int {
                        let m = Money(cents: 2)
                        return m(times: 4)
                    }
                    """
        );

        Assert.Equal(8L, Run(module, "indexed"));
        Assert.Equal(15L, Run(module, "called"));
        Assert.Equal(8L, Run(module, "labelled"));
    }

    [Fact]
    public void ACompoundAssignmentUsesTheBinaryOperator() {
        var module = Lower(
            Money + """

                    fn total() -> Int {
                        var m = Money(cents: 1)
                        m += Money(cents: 2)
                        m += Money(cents: 10)
                        m -= Money(cents: 4)
                        return m.cents
                    }
                    """
        );

        Assert.Equal(9L, Run(module, "total"));
    }

    [Fact]
    public void IdentityIsTheOneObject() {
        var module = Lower(
            Money + """

                    fn same() -> Bool {
                        let a = Box(v: 1)
                        let b = a
                        return a === b
                    }
                    fn twins() -> Bool { return Box(v: 1) === Box(v: 1) }
                    fn differ() -> Bool { return Box(v: 1) !== Box(v: 1) }
                    fn throughABase() -> Bool {
                        let c = Crate()
                        let b: Box = c
                        return b === c
                    }
                    """
        );

        Assert.Equal(true, Run(module, "same"));
        Assert.Equal(false, Run(module, "twins"));
        Assert.Equal(true, Run(module, "differ"));
        Assert.Equal(true, Run(module, "throughABase"));
    }

    [Fact]
    public void TuplesCompareElementByElement() {
        var module = Lower(
            Money + """

                    fn equal(_ a: Int, _ b: String) -> Bool { return (a, b) == (1, "x") }
                    fn unequal(_ a: Int, _ b: String) -> Bool { return (a, b) != (1, "x") }
                    fn nested() -> Bool { return ((1, "a"), Money(cents: 2)) == ((1, "a"), Money(cents: 2)) }
                    fn counted(_ limit: Int) -> Int {
                        var n = 0
                        while (n, "s") != (limit, "s") { n = n + 1 }
                        return n
                    }
                    """
        );

        Assert.Equal(true, Run(module, "equal", 1L, "x"));
        Assert.Equal(false, Run(module, "equal", 1L, "y"));
        Assert.Equal(true, Run(module, "unequal", 2L, "x"));
        Assert.Equal(false, Run(module, "unequal", 1L, "x"));
        Assert.Equal(true, Run(module, "nested"));
        Assert.Equal(4L, Run(module, "counted", 4L));
    }

    [Fact]
    public void OptionalsOfATypeCompareByItsOperator() {
        var module = Lower(
            Money + """

                    fn compare(_ a: Int, _ b: Int) -> Bool {
                        let x: Money? = a < 0 ? none : Money(cents: a)
                        let y: Money? = b < 0 ? none : Money(cents: b)
                        return x == y
                    }
                    fn withAValue() -> Bool {
                        let x: Money? = Money(cents: 3)
                        return x == Money(cents: 3) && x != Money(cents: 4)
                    }
                    fn ofTuples() -> Bool {
                        let x: (Int, String)? = (1, "a")
                        return x == (1, "a")
                    }
                    """
        );

        Assert.Equal(true, Run(module, "compare", 3L, 3L));
        Assert.Equal(false, Run(module, "compare", 3L, 4L));
        Assert.Equal(false, Run(module, "compare", 3L, -1L));
        Assert.Equal(false, Run(module, "compare", -1L, 3L));
        Assert.Equal(true, Run(module, "compare", -1L, -1L));
        Assert.Equal(true, Run(module, "withAValue"));
        Assert.Equal(true, Run(module, "ofTuples"));
    }
}
