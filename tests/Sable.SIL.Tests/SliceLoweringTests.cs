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
    ///     <c>xs[^n]</c> and a range with a bound counted from the end
    ///     (#291): the collection protocols' subscripts, an array's as any
    ///     collection's, trapping in the from-end form outside the array.
    /// </summary>
    [Fact]
    public void AnArrayIsReadAndSlicedFromTheEnd() {
        var module = Lower(
            Sum + """

                  fn last(_ n: Int) -> Int {
                      let xs = [10, 20, 30]
                      return xs[^n]
                  }
                  fn inner() -> Int {
                      let xs = [1, 2, 3, 4, 5]
                      return sum(xs[1..<^1])
                  }
                  fn tail(_ n: Int) -> Int {
                      let xs: Int[>..] = [1, 2, 3, 4, 5]
                      return sum(xs[^n...])
                  }
                  fn lastTwo() -> Int {
                      let xs: Int[] = [1, 2, 3, 4, 5]
                      return sum(xs[^2...^1]) * 100 + sum(xs[..<^3]) * 10 + sum(xs[...^5])
                  }
                  fn held(_ a: Int, _ b: Int) -> Int {
                      let xs = [1, 2, 3, 4, 5]
                      let r = a..<^b
                      return sum(xs[r])
                  }
                  """
        );

        Assert.Equal(30L, Run(module, "last", 1L));
        Assert.Equal(10L, Run(module, "last", 3L));
        Assert.Equal(9L, Run(module, "inner"));
        Assert.Equal(12L, Run(module, "tail", 3L));
        Assert.Equal(15L, Run(module, "tail", 5L));
        Assert.Equal(931L, Run(module, "lastTwo"));
        Assert.Equal(5L, Run(module, "held", 1L, 2L));
        var pastTheStart = Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.last", 4L));
        Assert.Equal("index ^4 is out of range for count 3", pastTheStart.Message);

        // Runtime.trap with no site says none, as the native trap does, not the library's line.
        Assert.Null(pastTheStart.Site);
        Assert.Equal("index ^0 is out of range for count 3", Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.last", 0L)).Message);
        Assert.Equal("range ^6..<^0 is out of range for count 5", Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.tail", 6L)).Message);
        Assert.Equal("range 4..<^2 is out of range for count 5", Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.held", 4L, 2L)).Message);
    }

    /// <summary>
    ///     The subscripts the collection protocols' extensions give are no
    ///     requirements, so a conformer's own method that happens to share
    ///     their getter's name, a <c>get(_:)</c>, is not called in their place.
    /// </summary>
    [Fact]
    public void AnExtensionsSubscriptIsNotAConformersMethodOfTheSameName() {
        var module = Lower(
            """
            struct Bag: RandomAccessCollection {
                var items: Int[>..]
                fn makeIterator() -> ArrayIterator<Int> { return items.makeIterator() }
                var startIndex: Int => 0
                var endIndex: Int => items.count
                var count: Int => items.count
                fn index(after position: Int) -> Int => position + 1
                fn index(before position: Int) -> Int => position - 1
                fn index(_ position: Int, offsetBy distance: Int) -> Int => position + distance
                fn distance(from start: Int, to end: Int) -> Int => end - start
                subscript(position: Int) -> Int { get => items[position] }
                fn get(_ key: String) -> Int? { return none }
            }
            fn last() -> Int {
                let bag = Bag { items: [4, 5, 6] }
                return bag[^1] * 10 + bag[^3]
            }
            """
        );

        Assert.Equal(64L, Run(module, "last"));
    }

    /// <summary>
    ///     An array pays nothing for counting from the end: after the
    ///     optimising passes, no FromEnd or RelativeRange is made, nothing is
    ///     allocated and nothing called but the out-of-line trap — what is
    ///     left is arithmetic on the count, and the bounds check.
    /// </summary>
    [Fact]
    public void CountingFromTheEndOfAnArrayIsArithmeticOnItsCount() {
        // Of specialised code (#297): the inliner leaves unspecialised code alone.
        using var specialised = SilGen.UseGenerics(GenericsMode.Specialised);
        var module = Lower(
            """
            fn last(_ xs: Int[:]) -> Int { return xs[^1] }
            fn back(_ xs: Int[:], _ n: Int) -> Int { return xs[^n] }
            fn inner(_ xs: Int[:]) -> Int[:] { return xs[1..<^1] }
            fn tail(_ xs: Int[>..], _ n: Int) -> Int[:] { return xs[^n...] }
            fn lastTwo(_ xs: Int[:]) -> Int[:] { return xs[^2...^1] }
            fn run() -> Int {
                let xs: Int[>..] = [1, 2, 3, 4, 5]
                return last(xs) * 1000 + back(xs, 5) * 100 + inner(xs).count * 10 + tail(xs, 2)[0] + lastTwo(xs).count
            }
            """
        );
        SilPassManager.Run(module, SilPassManager.Optimising);

        foreach (var name in new[] { "last", "back", "inner", "tail", "lastTwo" }) {
            var instructions = module.Find($"M.{name}")!.Blocks.SelectMany(static b => b.Instructions).ToList();
            Assert.DoesNotContain(instructions, static i => i is StructInst { StructType.Name: var type } && (type.Contains("FromEnd") || type.Contains("RelativeRange")));
            Assert.DoesNotContain(instructions, static i => i is AllocRefInst or AllocStackInst);
            Assert.All(instructions.OfType<FunctionRefInst>(), static f => Assert.EndsWith(".outOfRange", f.Function, StringComparison.Ordinal));
            Assert.Contains(instructions, static i => i is BuiltinInst { Name: "array_count" });
            Assert.Contains(instructions, static i => i is BuiltinInst { Name: "array_get" or "array_slice" });
        }

        Assert.Equal(5136L, new SilInterpreter(module).Run("M.run"));
    }

    /// <summary>
    ///     Writing from the end (#293) is MutableCollection's subscript, which
    ///     a store chooses over RandomAccessCollection's read-only one; on an
    ///     array it too is no more than arithmetic on the count, the bounds
    ///     check and the store, after the optimising passes.
    /// </summary>
    [Fact]
    public void WritingFromTheEndOfAnArrayIsArithmeticOnItsCount() {
        // Of specialised code (#297): the inliner leaves unspecialised code alone.
        using var specialised = SilGen.UseGenerics(GenericsMode.Specialised);
        var module = Lower(
            """
            fn setLast(_ xs: inout Int[>..], _ v: Int) { xs[^1] = v }
            fn bumpBack(_ xs: inout Int[>..], _ n: Int) { xs[^n] += 1 }
            fn run() -> Int {
                var xs: Int[>..] = [1, 2, 3]
                setLast(&xs, 9)
                bumpBack(&xs, 3)
                return xs[0] * 100 + xs[1] * 10 + xs[^1]
            }
            fn pastTheStart(_ n: Int) {
                var xs: Int[>..] = [1, 2, 3]
                bumpBack(&xs, n)
            }
            """
        );
        SilPassManager.Run(module, SilPassManager.Optimising);

        foreach (var name in new[] { "setLast", "bumpBack" }) {
            var instructions = module.Find($"M.{name}")!.Blocks.SelectMany(static b => b.Instructions).ToList();
            Assert.DoesNotContain(instructions, static i => i is StructInst { StructType.Name: var type } && type.Contains("FromEnd"));
            Assert.DoesNotContain(instructions, static i => i is AllocRefInst or AllocStackInst);
            Assert.All(instructions.OfType<FunctionRefInst>(), static f => Assert.EndsWith(".outOfRange", f.Function, StringComparison.Ordinal));
            Assert.Contains(instructions, static i => i is BuiltinInst { Name: "array_count" });
            Assert.Contains(instructions, static i => i is BuiltinInst { Name: "array_set" });
        }

        Assert.Equal(229L, new SilInterpreter(module).Run("M.run"));
        Assert.Equal(
            "index ^4 is out of range for count 3",
            Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.pastTheStart", 4L)).Message
        );
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
