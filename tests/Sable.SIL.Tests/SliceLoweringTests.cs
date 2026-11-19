namespace Sable.SIL.Tests;

/// <summary>
///     Slices as programs compute them (SYNTAX.md §11): each kind of array
///     passed where a <c>T[:]</c> is wanted, and a range of an array's
///     elements read out with <c>xs[a..&lt;b]</c> and its one-sided forms.
/// </summary>
public sealed class SliceLoweringTests {
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

    const string Sum = """
                       fn sum(_ xs: Int[:]) -> Int {
                           var total = 0
                           for x in xs { total = total + x }
                           return total
                       }
                       fn first<T>(_ xs: T[:]) -> T { return xs[0] }
                       """;

    [Fact]
    public void EachKindOfArrayIsASlice() {
        var module = Lower(
            Sum + """

                  fn grow() -> Int {
                      let xs: Int[>..] = [1, 2, 3]
                      return sum(xs)
                  }
                  fn fixed() -> Int {
                      let xs: Int[] = [10, 20]
                      return sum(xs)
                  }
                  fn shrink() -> Int {
                      var xs: Int[>..<] = [7]
                      xs.append(8)
                      return sum(xs)
                  }
                  fn generic() -> String {
                      let names: String[>..] = ["ann", "bo"]
                      return first(names)
                  }
                  """
        );

        Assert.Equal(6L, Run(module, "grow"));
        Assert.Equal(30L, Run(module, "fixed"));
        Assert.Equal(15L, Run(module, "shrink"));
        Assert.Equal("ann", Run(module, "generic"));
    }

    [Fact]
    public void ARangeOfElementsIsASlice() {
        var module = Lower(
            Sum + """

                  fn halfOpen(_ a: Int, _ b: Int) -> Int {
                      let xs: Int[>..] = [1, 2, 3, 4, 5]
                      return sum(xs[a..<b])
                  }
                  fn closed(_ a: Int, _ b: Int) -> Int {
                      let xs: Int[>..] = [1, 2, 3, 4, 5]
                      return sum(xs[a...b])
                  }
                  fn fromStart() -> Int {
                      let xs: Int[] = [1, 2, 3, 4, 5]
                      return sum(xs[..<2]) * 100 + sum(xs[...1])
                  }
                  fn toEnd() -> Int {
                      let xs: Int[] = [1, 2, 3, 4, 5]
                      return sum(xs[2...])
                  }
                  fn byValue() -> Int {
                      let xs: Int[] = [1, 2, 3, 4, 5]
                      let r = 1..<4
                      let c = 0...1
                      return sum(xs[r]) * 100 + sum(xs[c])
                  }
                  fn strings() -> String {
                      let words: String[>..] = ["a", "b", "c"]
                      return first(words[1...])
                  }
                  """
        );

        Assert.Equal(5L, Run(module, "halfOpen", 1L, 3L));
        Assert.Equal(0L, Run(module, "halfOpen", 2L, 2L));
        Assert.Equal(9L, Run(module, "closed", 1L, 3L));
        Assert.Equal(303L, Run(module, "fromStart"));
        Assert.Equal(12L, Run(module, "toEnd"));
        Assert.Equal(903L, Run(module, "byValue"));
        Assert.Equal("b", Run(module, "strings"));
        Assert.Equal("array slice out of range", Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.halfOpen", 3L, 9L)).Message);
    }

    /// <summary>
    ///     A slice is a type of its own, <c>[T:]</c> — a view of an array's
    ///     buffer — that prints and parses back; an array passed where one is
    ///     wanted is viewed with <c>slice_of</c>, and a range is taken with
    ///     <c>array_slice</c>, of an array or of a slice, with no copy.
    /// </summary>
    [Fact]
    public void ASliceIsAViewOfItsOwnType() {
        var module = Lower(
            Sum + """

                  fn views() -> Int {
                      let xs: Int[>..] = [1, 2, 3, 4, 5]
                      let s = xs[1..<4]
                      return sum(xs) * 100 + sum(s[1...])
                  }
                  """
        );

        var text = SilPrinter.Print(module);
        Assert.Contains("sil @M.sum : $([Int:]) -> Int", text, StringComparison.Ordinal);
        Assert.Contains("builtin \"slice_of\"", text, StringComparison.Ordinal);
        Assert.Contains("builtin \"array_slice\"(", text, StringComparison.Ordinal);
        Assert.Contains(") : $[Int:]", text, StringComparison.Ordinal);

        // The array is the one allocation: the slices share its buffer.
        var interpreter = new SilInterpreter(module);
        Assert.Equal(1507L, interpreter.Run("M.views"));
        Assert.Equal(1, interpreter.Allocations);
        Assert.Equal(0, interpreter.LiveObjects);
    }

    /// <summary>
    ///     A mutating member of <c>Element[:]</c> called on an array is given
    ///     the array viewed whole, in a slot of its own, and the array is put
    ///     back from it after the call with <c>array_of_slice</c>: the buffer
    ///     itself, changed in place, with nothing copied.
    /// </summary>
    [Fact]
    public void AMutatingSliceMemberViewsTheArrayAndPutsItBack() {
        var module = Lower(
            """
            extension<Element> Element[:] {
                mutating fn swapPair(_ i: Int, _ j: Int) {
                    let held = self[i]
                    self[i] = self[j]
                    self[j] = held
                }
            }

            fn swapped() -> Int {
                var xs: Int[>..] = [1, 2, 3]
                var k = 0
                while k < 5 {
                    xs.swapPair(0, xs.count - 1)
                    k = k + 1
                }
                return xs[0] * 100 + xs[2]
            }
            """
        );

        var text = SilPrinter.Print(module);
        Assert.Contains("builtin \"array_of_slice\"", text, StringComparison.Ordinal);

        var interpreter = new SilInterpreter(module);
        Assert.Equal(301L, interpreter.Run("M.swapped"));
        Assert.Equal(1, interpreter.Allocations);
        Assert.Equal(0, interpreter.LiveObjects);
    }
}
