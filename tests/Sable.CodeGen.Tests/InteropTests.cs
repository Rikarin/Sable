using System.Diagnostics;

namespace Sable.CodeGen.Tests;

/// <summary>
///     C interop, built and run (SYNTAX.md §24): Sable calling C's functions
///     with C's calling convention, and C calling Sable's — scalars, and
///     structs of each shape the ABI passes differently.
/// </summary>
/// <remarks>Like every native test, these pass vacuously where there is no C compiler.</remarks>
public sealed class InteropTests {
    const string Structs = """
                           struct Small {
                               a: U8
                               b: Bool
                               c: I16
                           }

                           struct Mixed {
                               x: F64
                               n: I32
                           }

                           struct Big {
                               a: Int
                               b: Int
                               c: Int
                               d: I8
                           }

                           struct F3 {
                               a: F32
                               b: F32
                               c: F32
                           }

                           """;

    [Fact]
    public void SableCallsTheCLibrary() {
        if (!Programs.CanBuild) {
            return;
        }

        var run = Programs.BuildAndRun(
            """
            module Main

            struct DivT {
                quot: I32
                rem: I32
            }

            struct LDivT {
                quot: Int
                rem: Int
            }

            struct Complex {
                re: F64
                im: F64
            }

            @extern("c", "abs")
            fn cAbs(_ x: I32) -> I32

            @extern("c")
            fn labs(_ x: Int) -> Int

            @extern("c", "div")
            fn cDiv(_ a: I32, _ b: I32) -> DivT

            @extern("c", "ldiv")
            fn cLdiv(_ a: Int, _ b: Int) -> LDivT

            @extern("c", "cabs")
            fn cAbsComplex(_ z: Complex) -> F64

            fn main() {
                print(cAbs(-5))
                print(labs(-50000000000))
                let d = cDiv(17, 5)
                print("\(d.quot) \(d.rem)")
                let l = cLdiv(100000000000, 7)
                print("\(l.quot) \(l.rem)")
                print(cAbsComplex(Complex(re: 3.0, im: 4.0)))
            }
            """
        );

        Assert.Equal("5\n50000000000\n3 2\n14285714285 5\n5\n", run.Stdout);
    }

    [Fact]
    public void CCallsSableThroughItsExportedName() {
        if (!Programs.CanBuild) {
            return;
        }

        // C's own compiler on the other side: what it passes and expects is
        // the platform's convention, not this compiler's idea of it.
        const string c = """
                         #include <stdint.h>
                         #include <stdbool.h>
                         typedef struct { uint8_t a; bool b; int16_t c; } Small;
                         typedef struct { double x; int32_t n; } Mixed;
                         typedef struct { int64_t a, b, c; int8_t d; } Big;
                         typedef struct { float a, b, c; } F3;
                         Big sable_grow(Big big, int64_t by);
                         Mixed sable_mix(Mixed m, Small s);
                         bool sable_negative(int8_t n);
                         Big c_grow(Big big, int64_t by) { Big r = sable_grow(big, by); r.a += 1000; return r; }
                         Mixed c_mix(Mixed m, Small s) { Mixed r = sable_mix(m, s); r.n += 1000; return r; }
                         F3 c_rotate(F3 f) { F3 r = { f.c, f.a, f.b + 0.5f }; return r; }
                         Small c_small(Small s) { Small r = { (uint8_t)(s.a + 1), !s.b, (int16_t)(s.c * 2) }; return r; }
                         int32_t c_negatives(int8_t a, int8_t b) { return sable_negative(a) + sable_negative(b); }
                         """;

        var source = $$"""
                       module Main

                       {{Structs}}
                       @export("c", "sable_grow")
                       fn grow(_ big: Big, _ by: Int) -> Big {
                           return Big(a: big.a + by, b: big.b + by, c: big.c + by, d: big.d + 1)
                       }

                       @export("c", "sable_mix")
                       fn mix(_ m: Mixed, _ s: Small) -> Mixed {
                           return Mixed(x: m.x * 2.0, n: m.n + I32(s.c) + I32(s.a))
                       }

                       @export("sable_negative")
                       fn negative(_ n: I8) -> Bool => n < 0

                       @extern("c", "c_grow")
                       fn cGrow(_ big: Big, _ by: Int) -> Big

                       @extern("c", "c_mix")
                       fn cMix(_ m: Mixed, _ s: Small) -> Mixed

                       @extern("c")
                       fn c_rotate(_ f: F3) -> F3

                       @extern("c")
                       fn c_small(_ s: Small) -> Small

                       @extern("c")
                       fn c_negatives(_ a: I8, _ b: I8) -> I32

                       fn main() {
                           let g = cGrow(Big(a: 1, b: 2, c: 3, d: 4), 10)
                           print("\(g.a) \(g.b) \(g.c) \(g.d)")
                           let m = cMix(Mixed(x: 1.5, n: 7), Small(a: 200, b: true, c: -3))
                           print("\(m.x) \(m.n)")
                           let f = c_rotate(F3(a: 1.0, b: 2.0, c: 3.0))
                           print("\(f.a) \(f.b) \(f.c)")
                           let s = c_small(Small(a: 41, b: false, c: -300))
                           print("\(s.a) \(s.b) \(s.c)")
                           print(c_negatives(-1, 1))
                       }
                       """;

        Assert.Equal("1011 12 13 5\n3 1204\n3 1 2.5\n42 true -600\n1\n", BuildWithC(source, c));
    }

    /// <summary>The program, with the C beside it, built and run: what it prints.</summary>
    static string BuildWithC(string source, string c) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));

        var directory = Directory.CreateTempSubdirectory("sable-interop-");
        try {
            var cSource = Path.Combine(directory.FullName, "side.c");
            File.WriteAllText(cSource, c);
            var cObject = Path.Combine(directory.FullName, "side.o");
            var compile = Process.Start(new ProcessStartInfo(NativeBuild.CompilerPath, ["-O2", "-c", cSource, "-o", cObject]) { RedirectStandardError = true })!;
            var errors = compile.StandardError.ReadToEnd();
            compile.WaitForExit();
            Assert.True(compile.ExitCode == 0, errors);

            var program = Path.Combine(directory.FullName, "program.o");
            var built = NativeObjects.CompileIr(result.LlvmIr!, program, true, false);
            Assert.True(built.Succeeded, built.Output);
            var runtime = Path.Combine(directory.FullName, "runtime.o");
            Assert.True(NativeObjects.CompileRuntime(runtime, true, false).Succeeded);
            var executable = Path.Combine(directory.FullName, "program");
            var linked = NativeObjects.Link([program, runtime, cObject, "-lm"], executable);
            Assert.True(linked.Succeeded, linked.Output);

            var run = Process.Start(new ProcessStartInfo(executable) { RedirectStandardOutput = true })!;
            var output = run.StandardOutput.ReadToEnd();
            run.WaitForExit();
            Assert.Equal(0, run.ExitCode);
            return output;
        } finally {
            directory.Delete(true);
        }
    }
}
