namespace Sable.SIL.Tests;

/// <summary>
///     Ranges as values: <c>a..&lt;b</c> and <c>a...b</c> build Sable.Core's
///     <c>Range</c> and <c>ClosedRange</c> structs, whose members are lowered
///     like any generic struct's; a <c>for</c> over one counts between its
///     bounds, and a <c>for</c> over a range written in the loop builds none.
/// </summary>
public sealed class RangeLoweringTests {
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

    const string Source = """
                          fn make(_ n: Int) -> Range<Int> {
                              return 0..<n
                          }

                          fn closed(_ a: Int, _ b: Int) -> ClosedRange<Int> {
                              return a...b
                          }

                          fn bounds(_ n: Int) -> Int {
                              let r = make(n)
                              return r.lowerBound * 100 + r.upperBound
                          }

                          fn sum(_ r: Range<Int>) -> Int {
                              var total = 0
                              for i in r {
                                  total = total + i
                              }
                              return total
                          }

                          fn sumTo(_ n: Int) -> Int {
                              return sum(make(n))
                          }

                          fn sumClosed(_ a: Int, _ b: Int) -> Int {
                              var total = 0
                              let c = closed(a, b)
                              for i in c where i % 2 == 0 {
                                  total = total + i
                              }
                              return total
                          }

                          fn empty(_ n: Int) -> Bool {
                              return make(n).isEmpty
                          }

                          fn closedEmpty(_ a: Int, _ b: Int) -> Bool {
                              return closed(a, b).isEmpty
                          }

                          fn has(_ n: Int, _ value: Int) -> Bool {
                              return make(n).contains(value)
                          }

                          fn closedHas(_ value: Int) -> Bool {
                              return closed(1, 5).contains(value)
                          }

                          fn words(_ word: String) -> Bool {
                              let r = "b"..<"d"
                              return r.contains(word)
                          }
                          """;

    [Fact]
    public void ARangeIsAStructOfItsBounds() {
        var module = Lower(Source);
        Assert.Equal(7L, Run(module, "bounds", 7L));
        Assert.NotNull(module.Find("M.make"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 10)]
    [InlineData(-3, 0)]
    public void AForOverARangeValueCounts(long n, long expected) =>
        Assert.Equal(expected, Run(Lower(Source), "sumTo", n));

    [Theory]
    [InlineData(1, 6, 12)]
    [InlineData(2, 2, 2)]
    [InlineData(3, 2, 0)]
    public void AForOverAClosedRangeValueIncludesTheUpperBound(long a, long b, long expected) =>
        Assert.Equal(expected, Run(Lower(Source), "sumClosed", a, b));

    [Fact]
    public void IsEmptyAndContains() {
        var module = Lower(Source);
        Assert.Equal(true, Run(module, "empty", 0L));
        Assert.Equal(true, Run(module, "empty", -1L));
        Assert.Equal(false, Run(module, "empty", 1L));
        Assert.Equal(false, Run(module, "closedEmpty", 2L, 2L));
        Assert.Equal(true, Run(module, "closedEmpty", 3L, 2L));
        Assert.Equal(true, Run(module, "has", 5L, 0L));
        Assert.Equal(true, Run(module, "has", 5L, 4L));
        Assert.Equal(false, Run(module, "has", 5L, 5L));
        Assert.Equal(false, Run(module, "has", 5L, -1L));
        Assert.Equal(true, Run(module, "closedHas", 5L));
        Assert.Equal(false, Run(module, "closedHas", 6L));
        Assert.Equal(true, Run(module, "words", "c"));
        Assert.Equal(false, Run(module, "words", "d"));
    }

    [Fact]
    public void AForOverARangeWrittenInTheLoopBuildsNoRange() {
        var module = Lower("""
                           fn count(_ n: Int) -> Int {
                               var total = 0
                               for i in 0..<n { total = total + i }
                               return total
                           }
                           """);

        Assert.Equal(45L, Run(module, "count", 10L));
        Assert.DoesNotContain("struct", SilPrinter.Print(module.Find("M.count")!));
    }

    /// <summary>
    ///     <c>for i in a... { }</c> counts up from <c>a</c> with no bound,
    ///     until the loop is left — and past <c>Int.max</c> it traps.
    /// </summary>
    [Fact]
    public void AForOverAnOpenRangeCountsUntilItIsLeft() {
        var module = Lower("""
                           fn firstSquareOver(_ limit: Int) -> Int {
                               for i in 1... {
                                   if i * i > limit { return i }
                               }
                               return 0
                           }

                           fn summed(from start: Int, _ n: Int) -> Int {
                               var total = 0
                               for i in start... where i % 2 == 0 {
                                   if i > start + n { break }
                                   total = total + i
                               }
                               return total
                           }
                           """);

        Assert.Equal(8L, Run(module, "firstSquareOver", 50L));
        Assert.Equal(2L + 4 + 6, Run(module, "summed", 1L, 6L));
        Assert.Throws<SilTrap>(() => Run(module, "summed", 9223372036854775806L, 6L));
    }

    /// <summary>
    ///     A range of a sized integer counts in its own type, so stepping past
    ///     its largest value traps; a range of characters counts their code
    ///     points, stepping over the surrogates.
    /// </summary>
    [Fact]
    public void ARangeOfSizedIntegersOrCharactersCounts() {
        var module = Lower("""
                           fn bytes(_ low: U8) -> Int {
                               var total = 0
                               for b in low...255 { total = total + Int(b) }
                               return total
                           }

                           fn open(_ low: U8) -> Int {
                               var total = 0
                               for b in low... { total = total + Int(b) }
                               return total
                           }

                           fn letters() -> Int {
                               var n = 0
                               for c in 'a'..<'e' { n = n * 10 + (c.codePoint - 96) }
                               return n
                           }

                           fn acrossSurrogates() -> Int {
                               var n = 0
                               for c in '\u{D7FE}'...'\u{E001}' { n = n + 1 }
                               return n
                           }
                           """);

        Assert.Equal(253L + 254 + 255, Run(module, "bytes", 253L));
        Assert.Throws<SilTrap>(() => Run(module, "open", 254L));
        Assert.Equal(1234L, Run(module, "letters"));
        Assert.Equal(4L, Run(module, "acrossSurrogates"));
        Assert.DoesNotContain("struct", SilPrinter.Print(module.Find("M.bytes")!));
    }

    [Fact]
    public void AOneSidedRangeIsAStructOfItsOneBound() {
        var module = Lower("""
                           fn upTo(_ n: Int, _ x: Int) -> Bool {
                               let r = ..<n
                               return r.contains(x)
                           }

                           fn through(_ n: Int, _ x: Int) -> Bool {
                               let r = ...n
                               return r.contains(x)
                           }

                           fn from(_ n: Int, _ limit: Int) -> Int {
                               let r = n...
                               var total = 0
                               for i in r {
                                   if i > limit { break }
                                   total = total + i
                               }
                               return total
                           }
                           """);

        Assert.Equal(true, Run(module, "upTo", 5L, 4L));
        Assert.Equal(false, Run(module, "upTo", 5L, 5L));
        Assert.Equal(true, Run(module, "through", 5L, 5L));
        Assert.Equal(3L + 4 + 5, Run(module, "from", 3L, 5L));
    }
}
