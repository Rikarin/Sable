namespace Sable.SIL.Tests;

/// <summary>
///     Generators (spec/SYNTAX.md §5): the coroutine split. The body is one
///     resume function whose slots are the iterator's fields and whose entry
///     branches on the state; the iterator is a struct, so going through a
///     generator over a range allocates nothing and calls nothing indirectly.
/// </summary>
public sealed class GeneratorLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create(source);
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => d.Message)));
        Assert.Empty(SilVerifier.Verify(module));
        return module;
    }

    const string Source = """
                          module M
                          fn evens(upTo n: Int) -> some Sequence<Int> {
                              var i = 0
                              while i <= n {
                                  yield i
                                  i += 2
                              }
                          }
                          fn squares<T: Numeric>(_ values: T[]) -> some Sequence<T> {
                              for v in values {
                                  yield v * v
                              }
                          }
                          fn sumEvens(_ n: Int) -> Int {
                              var sum = 0
                              for x in evens(upTo: n) {
                                  sum += x
                              }
                              return sum
                          }
                          fn sumSquares(_ n: Int) -> Int {
                              var sum = 0
                              for x in squares([1, 2, n]) {
                                  sum += x
                              }
                              return sum
                          }
                          fn overRange(_ n: Int) -> some Sequence<Int> {
                              for i in 0..<n {
                                  if i % 3 == 0 {
                                      yield i
                                  }
                              }
                          }
                          fn sumOverRange(_ n: Int) -> Int {
                              var sum = 0
                              for x in overRange(n) {
                                  sum += x
                              }
                              return sum
                          }
                          """;

    [Fact]
    public void TheBodyIsOneResumeFunctionWhoseSlotsAreTheIteratorsFields() {
        var module = Lower(Source);
        var resume = Assert.Single(module.Functions, static f => f.Name.StartsWith("M.evens$", StringComparison.Ordinal) && f.Name.EndsWith("$resume", StringComparison.Ordinal));
        var instructions = resume.Blocks.SelectMany(static b => b.Instructions).ToList();
        Assert.Empty(instructions.OfType<AllocStackInst>());
        Assert.Empty(instructions.OfType<DeallocStackInst>());

        // The iterator: n, the state, and i.
        var iterator = (StructType)((AddressType)resume.Type.Parameters[0]).Pointee;
        Assert.Equal(["n", "$state"], iterator.Fields.Take(2).Select(static f => f.Name));
        Assert.Contains(iterator.Fields, static f => f.Name.StartsWith("i$", StringComparison.Ordinal));

        Assert.Equal(0L + 2 + 4 + 6, new SilInterpreter(module).Run("M.sumEvens", 7L));
        Assert.Equal(1L + 4 + 25, new SilInterpreter(module).Run("M.sumSquares", 5L));
        Assert.Equal(0L + 3 + 6 + 9, new SilInterpreter(module).Run("M.sumOverRange", 10L));
    }

    [Fact]
    public void AGeneratorOverARangeAllocatesNothingAndCallsNothingIndirectly() {
        var module = Lower(Source);
        SilPassManager.Run(module, SilPassManager.Optimising);
        Assert.Empty(SilVerifier.Verify(module));

        foreach (var name in (string[])["M.sumEvens", "M.sumOverRange"]) {
            var instructions = module.Find(name)!.Blocks.SelectMany(static b => b.Instructions).ToList();
            Assert.Empty(instructions.OfType<AllocRefInst>());
            Assert.Empty(instructions.OfType<PartialApplyInst>());
            Assert.Empty(instructions.OfType<ClassMethodInst>());
            Assert.All(instructions.OfType<ApplyInst>(), static apply => Assert.IsType<FunctionRefInst>(apply.Callee));
        }

        Assert.Equal(0L + 3 + 6 + 9, new SilInterpreter(module).Run("M.sumOverRange", 10L));
    }

    [Fact]
    public void ARangesBoundIsKeptInASlotAcrossAYield() {
        // Every construct keeps what it needs across a statement in a slot;
        // a range's bound, held in a register by a plain loop, is one too.
        var module = Lower("""
                           module M
                           fn grid(_ w: Int, _ h: Int) -> some Sequence<Int> {
                               for y in 0..<h {
                                   for x in 0..<w {
                                       yield x + y * w
                                   }
                               }
                           }
                           fn count(_ w: Int, _ h: Int) -> Int {
                               var n = 0
                               for _ in grid(w, h) {
                                   n += 1
                               }
                               return n
                           }
                           """);
        Assert.Equal(12L, new SilInterpreter(module).Run("M.count", 3L, 4L));
    }

    [Fact]
    public void ABodyWithADeferKeepsItsStateInAnObjectWhoseDeinitLeavesTheBody() {
        var module = Lower("""
                           module M
                           fn guarded(_ n: Int) -> some Sequence<Int> {
                               defer {
                                   print("done")
                               }
                               for i in 0..<n {
                                   yield i
                               }
                           }
                           fn firstOnly(_ n: Int) -> Int {
                               for x in guarded(n) {
                                   return x + 1
                               }
                               return 0
                           }
                           """);
        var frame = Assert.Single(module.Types.OfType<ClassType>(), static t => t.Name.EndsWith("$Iterator$frame", StringComparison.Ordinal));
        Assert.NotNull(frame.Deinit);
        Assert.NotNull(module.Find(frame.Deinit!));
        var interpreter = new SilInterpreter(module);
        Assert.Equal(1L, interpreter.Run("M.firstOnly", 3L));
        Assert.Equal("done\n", interpreter.Output.ToString());
    }
}
