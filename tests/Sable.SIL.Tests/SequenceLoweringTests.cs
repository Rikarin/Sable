namespace Sable.SIL.Tests;

/// <summary>
///     What iteration costs once lowered. A <c>for</c> over an array or a
///     range is the compiler's own counted loop, as it was before arrays and
///     ranges were sequences: no iterator, no call, nothing allocated. Generic
///     code over a Sequence, given an array or a range, is monomorphised for
///     it: its iterator is a struct on the stack, and every call it makes is
///     to a known function, none through a closure or a witness table.
/// </summary>
public sealed class SequenceLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create(source);
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => d.Message)));
        Assert.Empty(SilVerifier.Verify(module));
        return module;
    }

    static IReadOnlyList<SilInstruction> Instructions(SilModule module, string function) =>
        [..(module.Find(function) ?? throw new InvalidOperationException($"no {function} in {string.Join(", ", module.Functions.Select(static f => f.Name))}"))
            .Blocks.SelectMany(static b => b.Instructions)];

    const string Source = """
                          module M
                          fn upTo(_ n: Int) -> Int[>..] {
                              var values: Int[>..] = []
                              var i = 1
                              while i <= n {
                                  values.append(i)
                                  i += 1
                              }
                              return values
                          }
                          fn overArray(_ values: Int[>..]) -> Int {
                              var sum = 0
                              for value in values {
                                  sum += value
                              }
                              return sum
                          }
                          fn overRange(_ n: Int) -> Int {
                              var sum = 0
                              for i in 0..<n {
                                  sum += i
                              }
                              return sum
                          }
                          fn total<S: Sequence>(_ values: S) -> Int where S.Element == Int {
                              var sum = 0
                              for value in values {
                                  sum += value
                              }
                              return sum
                          }
                          fn generic(_ n: Int) -> Int {
                              return total(upTo(n)) + total(0..<n)
                          }
                          fn counted(_ n: Int) -> Int {
                              return overArray(upTo(n)) + overRange(n)
                          }
                          """;

    [Fact]
    public void AForOverAnArrayOrARangeIsACountedLoop() {
        var module = Lower(Source);
        foreach (var function in (string[])["M.overArray", "M.overRange"]) {
            var instructions = Instructions(module, function);
            Assert.Empty(instructions.OfType<ApplyInst>());
            Assert.Empty(instructions.OfType<AllocRefInst>());
            Assert.Empty(instructions.OfType<PartialApplyInst>());
        }

        Assert.Equal(15L + 10L, new SilInterpreter(module).Run("M.counted", 5L));
    }

    [Fact]
    public void GenericIterationIsMonomorphisedWithNothingIndirect() {
        var module = Lower(Source);

        var instances = module.Functions.Where(static f => f.Name.StartsWith("M.total$of$", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, instances.Count);
        foreach (var instance in instances) {
            var instructions = instance.Blocks.SelectMany(static b => b.Instructions).ToList();

            // The iterator is a value on the stack: nothing on the heap.
            Assert.Empty(instructions.OfType<AllocRefInst>());
            Assert.Empty(instructions.OfType<PartialApplyInst>());

            // Every call is to a function known by name: the iterator's own
            // 'makeIterator' and 'next', not a witness looked up at run time.
            Assert.All(instructions.OfType<ApplyInst>(), static apply => Assert.IsType<FunctionRefInst>(apply.Callee));
        }

        Assert.Equal(15L + 10L, new SilInterpreter(module).Run("M.generic", 5L));
    }
}
