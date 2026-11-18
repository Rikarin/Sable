namespace Sable.SIL.Tests;

/// <summary>
///     The array family's members, declared in Sable (lib/Sable.Core/Array.sb)
///     by array extensions: the <c>@builtin</c>s in place at each use, as
///     the compiler's own lowering was; the rest monomorphised per element
///     type, as any generic is; both run by the interpreter.
/// </summary>
public sealed class ArrayMemberLoweringTests {
    static SilModule Lower(params string[] sources) {
        var compilation = Compilation.Create(sources);
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => d.Message)));
        Assert.Empty(SilVerifier.Verify(module));
        return module;
    }

    static IEnumerable<SilInstruction> Instructions(SilModule module, string function) =>
        module.Find(function)!.Blocks.SelectMany(static b => b.Instructions);

    [Fact]
    public void TheBuiltinMembersAreTheBuiltinsInPlace() {
        var module = Lower(
            """
            module M
            fn churn(_ n: Int) -> Int {
                var xs: Int[>..] = []
                var i = 0
                while i < n {
                    xs.append(i)
                    i = i + 1
                }
                let last = xs.removeLast()
                let part = xs.slice(from: 1, to: 3)
                let counted = xs.count + part.count
                xs.clear()
                return last * 100 + counted * 10 + xs.count
            }
            """
        );

        var builtins = Instructions(module, "M.churn").OfType<BuiltinInst>().Select(static b => b.Name).Where(static n => n.StartsWith("array_", StringComparison.Ordinal)).ToList();
        Assert.Equal(["array_empty", "array_append", "array_remove_last", "array_slice", "array_count", "array_count", "array_clear", "array_count"], builtins);

        // Nothing is called for them, and nothing made.
        Assert.Empty(Instructions(module, "M.churn").OfType<ApplyInst>());
        Assert.DoesNotContain(module.Functions, static f => f.Name.Contains("append", StringComparison.Ordinal) || f.Name.Contains("count", StringComparison.Ordinal));

        Assert.Equal(9 * 100 + (9 + 2) * 10 + 0L, new SilInterpreter(module).Run("M.churn", 10L));
    }

    [Fact]
    public void TheSableMembersAreMonomorphisedPerElement() {
        var module = Lower(
            """
            module M
            fn probe(_ n: Int) -> Int {
                var xs: Int[>..] = []
                var names: String[>..<] = []
                var i = 0
                while i < n {
                    xs.append(i * 2)
                    names.append("n\(i)")
                    i = i + 1
                }
                var score = 0
                if xs.contains(4) { score = score + 1 }
                if !xs.contains(5) { score = score + 10 }
                if names.contains("n2") { score = score + 100 }
                if !names.isEmpty { score = score + 1000 }
                let empty: Int[] = []
                if empty.isEmpty && !empty.contains(1) { score = score + 10000 }
                return score
            }
            """
        );

        var called = Instructions(module, "M.probe").OfType<FunctionRefInst>().Select(static f => f.Function).ToHashSet();
        Assert.Contains(called, static f => f.Contains("contains", StringComparison.Ordinal) && f.EndsWith("$of$Int", StringComparison.Ordinal));
        Assert.Contains(called, static f => f.Contains("contains", StringComparison.Ordinal) && f.EndsWith("$of$String", StringComparison.Ordinal));

        Assert.Equal(11111L, new SilInterpreter(module).Run("M.probe", 4L));
        Assert.Equal(10010L, new SilInterpreter(module).Run("M.probe", 0L));
    }

    [Fact]
    public void BytesReadAsText() {
        var module = Lower(
            """
            module M
            fn text() -> String {
                let bytes: U8[>..] = [104, 195, 169, 226, 130, 172, 240, 159, 152, 128, 33, 255]
                return bytes.toString()
            }
            """
        );

        Assert.Equal("hé€\U0001F600!�", new SilInterpreter(module).Run("M.text"));
    }

    [Fact]
    public void AProgramsOwnArrayExtensionLowers() {
        var module = Lower(
            """
            module M
            extension<T: Comparable> T[:] {
                fn largest() -> T {
                    var best = self[0]
                    for x in self {
                        if best < x {
                            best = x
                        }
                    }
                    return best
                }
            }
            extension<T> T[>..] {
                mutating fn pushTwice(_ item: T) {
                    append(item)
                    self.append(item)
                }
                fn doubled() -> Int { return count * 2 }
            }
            fn probe() -> Int {
                var xs: Int[>..] = [3, 9, 4]
                xs.pushTwice(1)
                var words: String[>..] = ["b", "c", "a"]
                let top: String = words.largest()
                return xs.largest() * 100 + xs.doubled() * 10 + top.count
            }
            """
        );

        Assert.Equal(9 * 100 + 10 * 10 + 1L, new SilInterpreter(module).Run("M.probe"));
    }

    [Theory]
    [InlineData("extension<E> E[>..] {\n    @builtin(\"array_append\")\n    public fn add(element: E)\n}", "(*[Int], Int) -> ()")]
    [InlineData("extension<E> E[:] {\n    @builtin(\"array_count\")\n    public var size: Bool { get }\n}", "-> Bool")]
    [InlineData("extension<E> E[>..] {\n    @builtin(\"array_remove_last\")\n    public mutating fn take() -> Bool\n}", "-> Bool")]
    public void AnArrayBuiltinThatDisagreesIsAnError(string core, string message) {
        var compilation = Compilation.Create($"module Sable.Core\n{core}", "module M");
        var (_, diagnostics) = SilGen.Lower(compilation);
        var error = Assert.Single(diagnostics);
        Assert.Equal(ErrorCode.ERR_BuiltinSignatureMismatch, error.Code);
        Assert.Contains(message, error.Message);
    }
}
