using System.Diagnostics;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Atomics: the interpreter and the native code agree, and an atomic
///     operation with a constant ordering is one instruction of that ordering
///     once LLVM has optimised it — the switch over orderings folded away.
/// </summary>
[Native]
public sealed class AtomicTests {
    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void TheInterpreterAndTheNativeCodeAgree(GenericsMode generics) {
        using var _ = SilGen.UseGenerics(generics);
        var run = AssertAgree("""
            module P
            fn twice<T: AtomicInteger>(_ value: Atomic<T>, _ amount: T) -> T {
                value.add(amount)
                return value.add(amount, ordering: .AcquireRelease)
            }
            @main
            fn main() {
                let n = Atomic<Int>(1)
                print(twice(n, 20))
                print(n.compareExchange(expected: 41, desired: 7).exchanged)
                print(n.update { $0 * 3 })
                let b = Atomic<U8>(255)
                print(twice(b, 1))
                print(b.bitwiseXor(0xFF))
                let flag = Atomic<Bool>(false)
                print(flag.logicalOr(true))
                print(flag.load(ordering: .Acquire))
                let cell = OnceCell<String>()
                print(cell.getOrInit { "once" } + (cell.get() ?? "-"))
            }
            """);
        Assert.Equal("21\ntrue\n7\n0\n1\nfalse\ntrue\nonceonce\n", run.Stdout);
    }

    [Fact]
    public void AConstantOrderingIsOneInstruction() {
        if (!CanBuild) {
            return;
        }

        var directory = Directory.CreateTempSubdirectory("sable-atomic");
        try {
            var source = Path.Combine(directory.FullName, "atomic.sb");
            File.WriteAllText(source, """
                module G
                fn bump(_ counter: Atomic<Int>) -> Int {
                    return counter.add(1, ordering: .Relaxed)
                }
                @main
                fn main() {
                    print(bump(Atomic<Int>(0)))
                }
                """);
            var emitted = SableProcess.Run(directory.FullName, directory.FullName, "build", source, "--emit", "llvm-ir", "-O2");
            Assert.Equal(0, emitted.ExitCode);
            var ir = Path.Combine(directory.FullName, "atomic.ll");
            var optimised = Path.Combine(directory.FullName, "atomic.opt.ll");
            File.WriteAllText(ir, emitted.Output);
            var clang = Process.Start(new ProcessStartInfo(NativeBuild.CompilerPath, ["-O2", "-S", "-emit-llvm", "-Wno-override-module", ir, "-o", optimised]) { RedirectStandardError = true })!;
            clang.WaitForExit();
            Assert.Equal(0, clang.ExitCode);

            var adds = File.ReadAllLines(optimised).Where(static l => l.Contains("atomicrmw add", StringComparison.Ordinal)).ToList();
            var add = Assert.Single(adds);
            Assert.Contains("i64 1 monotonic", add, StringComparison.Ordinal);
        } finally {
            directory.Delete(true);
        }
    }
}
