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
            fn llabs(_ x: Int) -> Int

            @extern("c", "div")
            fn cDiv(_ a: I32, _ b: I32) -> DivT

            @extern("c", "lldiv")
            fn cLdiv(_ a: Int, _ b: Int) -> LDivT

            @extern("c", "cabs")
            fn cAbsComplex(_ z: Complex) -> F64

            fn main() {
                print(cAbs(-5))
                print(llabs(-50000000000))
                let d = cDiv(17, 5)
                print("\(d.quot) \(d.rem)")
                let l = cLdiv(100000000000, 7)
                print("\(l.quot) \(l.rem)")
                print(cAbsComplex(Complex(re: 3.0, im: 4.0)))
            }
            """
        );

        Assert.Equal("5\n50000000000\n3 2\n14285714285 5\n5.0\n", run.Stdout);
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

        Assert.Equal("1011 12 13 5\n3.0 1204\n3.0 1.0 2.5\n42 true -600\n1\n", BuildWithC(source, c));
    }

    // Shapes System V x86-64 passes in registers only while enough are left.
    const string RegisterStructs = """
                                   struct Pair {
                                       a: Int
                                       b: Int
                                   }

                                   struct DPair {
                                       x: F64
                                       y: F64
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

                                   """;

    const string RegisterC = """
                             #include <stdint.h>
                             typedef struct { int64_t a, b; } Pair;
                             typedef struct { double x, y; } DPair;
                             typedef struct { double x; int32_t n; } Mixed;
                             typedef struct { int64_t a, b, c; int8_t d; } Big;
                             int64_t sable_five(int64_t a, int64_t b, int64_t c, int64_t d, int64_t e, Pair p, int64_t f);
                             int64_t c_five(int64_t a, int64_t b, int64_t c, int64_t d, int64_t e, Pair p, int64_t f) {
                                 return a + b + c + d + e + p.a * 100 + p.b * 1000 + f * 10000;
                             }
                             double c_many(double a, double b, double c, double d, double e, double f, double g, DPair p, double h) {
                                 return a + b + c + d + e + f + g + p.x * 100 + p.y * 1000 + h * 10000;
                             }
                             Mixed c_tail(int64_t a, int64_t b, int64_t c, int64_t d, int64_t e, Mixed m) {
                                 Mixed r = { m.x + (double)(a + b + c + d + e), m.n * 2 };
                                 return r;
                             }
                             Big c_sret(int64_t a, int64_t b, int64_t c, int64_t d, Pair p) {
                                 Big r = { a + b + c + d, p.a, p.b, 9 };
                                 return r;
                             }
                             int64_t c_calls_five(void) { Pair p = { 6, 7 }; return sable_five(1, 2, 3, 4, 5, p, 8); }
                             """;

    const string RegisterProgram = $$"""
                                     module Main

                                     {{RegisterStructs}}
                                     @extern("c")
                                     fn c_five(_ a: Int, _ b: Int, _ c: Int, _ d: Int, _ e: Int, _ p: Pair, _ f: Int) -> Int

                                     @extern("c")
                                     fn c_many(_ a: F64, _ b: F64, _ c: F64, _ d: F64, _ e: F64, _ f: F64, _ g: F64, _ p: DPair, _ h: F64) -> F64

                                     @extern("c")
                                     fn c_tail(_ a: Int, _ b: Int, _ c: Int, _ d: Int, _ e: Int, _ m: Mixed) -> Mixed

                                     @extern("c")
                                     fn c_sret(_ a: Int, _ b: Int, _ c: Int, _ d: Int, _ p: Pair) -> Big

                                     @extern("c")
                                     fn c_calls_five() -> Int

                                     @export("sable_five")
                                     fn five(_ a: Int, _ b: Int, _ c: Int, _ d: Int, _ e: Int, _ p: Pair, _ f: Int) -> Int {
                                         return a + b + c + d + e + p.a * 100 + p.b * 1000 + f * 10000
                                     }

                                     fn main() {
                                         print(c_five(1, 2, 3, 4, 5, Pair(a: 6, b: 7), 8))
                                         print(c_many(1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, DPair(x: 8.0, y: 9.0), 10.0))
                                         let m = c_tail(1, 2, 3, 4, 5, Mixed(x: 1.5, n: 7))
                                         print("\(m.x) \(m.n)")
                                         let s = c_sret(1, 2, 3, 4, Pair(a: 5, b: 6))
                                         print("\(s.a) \(s.b) \(s.c) \(s.d)")
                                         print(c_calls_five())
                                     }
                                     """;

    const string RegisterOutput = "87615\n109828.0\n16.5 14\n10 5 6 9\n87615\n";

    /// <summary>
    ///     A struct whose parts no longer all fit in the registers left goes
    ///     whole on the stack, as System V x86-64 says — after five integers,
    ///     a pair of them; after seven doubles, a pair of those; after an
    ///     sret pointer and four integers, a pair again — while one that fits
    ///     the registers left still goes in them. Run on this machine, and on
    ///     x86-64 where this one can run it (natively, or under Rosetta).
    /// </summary>
    [Fact]
    public void AStructGoesWholeOnTheStackWhenTheRegistersRunOut() {
        if (!Programs.CanBuild) {
            return;
        }

        Assert.Equal(RegisterOutput, BuildWithC(RegisterProgram, RegisterC));
        if (NativeTarget.Host.Arch != "x86_64" && OperatingSystem.IsMacOS() && Rosetta()) {
            Assert.Equal(RegisterOutput, BuildWithC(RegisterProgram, RegisterC, NativeTarget.Of("x86_64-apple-darwin")));
        }
    }

    const string LayoutC = """
                           #include <stdint.h>
                           #include <stddef.h>
                           typedef struct __attribute__((packed)) { uint8_t tag; uint32_t length; uint16_t flags; } Header;
                           typedef struct { _Alignas(16) float x; float y; } Vec;
                           typedef struct { uint8_t a; Vec v; Header h; } Holder;
                           Header sable_header(Header h);
                           Header c_header(Header h) { Header r = sable_header(h); r.length += 1000; return r; }
                           Vec c_vec(Vec v) { Vec r = { v.y, v.x + 1 }; return r; }
                           Holder c_holder(Holder h) { h.a += 1; h.v.y += 1; h.h.length += 2; return h; }
                           int64_t c_layout(void) {
                               return (int64_t)(sizeof(Header) * 1000000 + sizeof(Vec) * 10000 + sizeof(Holder) * 100 + offsetof(Holder, h));
                           }
                           """;

    const string LayoutProgram = """
                                 module Main

                                 @packed
                                 struct Header {
                                     var tag: U8
                                     var length: U32
                                     var flags: U16
                                 }

                                 @align(16)
                                 struct Vec {
                                     var x: F32
                                     var y: F32
                                 }

                                 struct Holder {
                                     var a: U8
                                     var v: Vec
                                     var h: Header
                                 }

                                 @export("c", "sable_header")
                                 fn header(_ h: Header) -> Header {
                                     return Header(tag: h.tag + 1, length: h.length * 2, flags: h.flags + 3)
                                 }

                                 @extern("c")
                                 fn c_header(_ h: Header) -> Header

                                 @extern("c")
                                 fn c_vec(_ v: Vec) -> Vec

                                 @extern("c")
                                 fn c_holder(_ h: Holder) -> Holder

                                 @extern("c")
                                 fn c_layout() -> Int

                                 fn main() {
                                     let h = c_header(Header(tag: 7, length: 21, flags: 40000))
                                     print("\(h.tag) \(h.length) \(h.flags)")
                                     let v = c_vec(Vec(x: 1.5, y: 2.5))
                                     print("\(v.x) \(v.y)")
                                     let held = c_holder(Holder(a: 1, v: Vec(x: 3, y: 4), h: Header(tag: 9, length: 70000, flags: 1)))
                                     print("\(held.a) \(held.v.x) \(held.v.y) \(held.h.tag) \(held.h.length) \(held.h.flags)")
                                     print(c_layout())
                                 }
                                 """;

    /// <summary>
    ///     '@packed' and '@align(16)' lay a struct out as C's packed and
    ///     '_Alignas(16)' ones are, and C passes them as it would its own: a
    ///     packed struct with misaligned fields in memory on x86-64, an
    ///     over-aligned one in an even pair of registers on arm64, a struct
    ///     holding both, padded, through memory.
    /// </summary>
    [Fact]
    public void PackedAndAlignedStructsAreCs() {
        if (!Programs.CanBuild) {
            return;
        }

        const string output = "8 1042 40003\n2.5 2.5\n2 3.0 5.0 9 70002 1\n7164832\n";
        Assert.Equal(output, BuildWithC(LayoutProgram, LayoutC));
        if (NativeTarget.Host.Arch != "x86_64" && OperatingSystem.IsMacOS() && Rosetta()) {
            Assert.Equal(output, BuildWithC(LayoutProgram, LayoutC, NativeTarget.Of("x86_64-apple-darwin")));
        }
    }

    /// <summary>The declarations each target's C sees, as clang declares the same C functions.</summary>
    [Theory]
    [InlineData("x86_64-unknown-linux-gnu", "declare i64 @\"c_five\"(i64, i64, i64, i64, i64, ptr byval({ i64, i64 }) align 8, i64)")]
    [InlineData("x86_64-unknown-linux-gnu", "declare double @\"c_many\"(double, double, double, double, double, double, double, ptr byval({ double, double }) align 8, double)")]
    [InlineData("x86_64-unknown-linux-gnu", "declare { double, i64 } @\"c_tail\"(i64, i64, i64, i64, i64, { double, i64 })")]
    [InlineData("x86_64-unknown-linux-gnu", "declare void @\"c_sret\"(ptr sret({ i64, i64, i64, i8 }) align 8, i64, i64, i64, i64, ptr byval({ i64, i64 }) align 8)")]
    [InlineData("wasm32-wasi", "declare void @\"c_tail\"(ptr sret({ double, i32 }) align 8, i64, i64, i64, i64, i64, ptr byval({ double, i32 }) align 8)")]
    [InlineData("wasm32-wasi", "declare i64 @\"c_five\"(i64, i64, i64, i64, i64, ptr byval({ i64, i64 }) align 8, i64)")]
    [InlineData("wasm32-wasi", "declare i32 @\"c_one\"(i32, ptr byval({ float, float, float }) align 4)")]
    [InlineData("wasm32-wasi", "declare void @\"c_f3\"(ptr sret({ float, float, float }) align 4, ptr byval({ float, float, float }) align 4)")]
    public void EachTargetPassesStructsAsItsC(string triple, string declaration) {
        const string extra = """
                             struct One {
                                 v: I32
                             }

                             struct F3 {
                                 a: F32
                                 b: F32
                                 c: F32
                             }

                             @extern("c")
                             fn c_one(_ o: One, _ f: F3) -> One

                             @extern("c")
                             fn c_f3(_ f: F3) -> F3

                             """;
        var target = NativeTarget.Of(triple);
        var source = RegisterProgram.Replace("fn main() {", extra + "fn main() {\n    print(c_one(One(v: 1), F3(a: 1.0, b: 2.0, c: 3.0)).v)\n    print(c_f3(F3(a: 1.0, b: 2.0, c: 3.0)).a)", StringComparison.Ordinal);
        var tree = SyntaxTree.Parse(new SourceText(source, "test.sb"), Pipeline.ParseOptionsFor(true, target));
        var result = Pipeline.Compile([tree], target: target);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains(declaration + "\n", result.LlvmIr!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A variadic C function (CVarArg...): each call declares it for the
    ///     types it passes, and calls it with C's '...' and its promotions.
    /// </summary>
    [Fact]
    public void SableCallsAVariadicCFunction() {
        if (!Programs.CanBuild) {
            return;
        }

        // C reads what follows its fixed parameters as its default
        // promotions leave it: an I8 and a Bool as an int, an F32 as a double.
        const string c = """
                         #include <stdarg.h>
                         #include <stdint.h>
                         int64_t c_add(int32_t n, ...) {
                             va_list args;
                             va_start(args, n);
                             int64_t total = 0;
                             for (int32_t i = 0; i < n; i++) total += va_arg(args, int64_t);
                             va_end(args);
                             return total;
                         }
                         double c_mix(int32_t tag, ...) {
                             va_list args;
                             va_start(args, tag);
                             int64_t a = va_arg(args, int64_t);
                             int b = va_arg(args, int);
                             double f = va_arg(args, double);
                             int t = va_arg(args, int);
                             uint32_t u = va_arg(args, uint32_t);
                             double d = va_arg(args, double);
                             va_end(args);
                             return tag * 1000000.0 + a * 1000 + b * 100 + f * 10 + t + u + d;
                         }
                         """;

        const string source = """
                              module Main

                              @extern("c")
                              fn c_add(_ n: I32, _ values: CVarArg...) -> Int

                              @extern("c")
                              fn c_mix(_ tag: I32, _ rest: CVarArg...) -> F64

                              fn main() {
                                  print(c_add(0))
                                  print(c_add(3, 1, 20, 300))
                                  let small: I8 = -3
                                  let half: F32 = 0.5
                                  let big: U32 = 4000000000
                                  print(c_mix(2, 7, small, half, true, big, 0.25))
                              }
                              """;

        Assert.Equal("0\n321\n4002006706.25\n", BuildWithC(source, c));
        if (NativeTarget.Host.Arch != "x86_64" && OperatingSystem.IsMacOS() && Rosetta()) {
            Assert.Equal("0\n321\n4002006706.25\n", BuildWithC(source, c, NativeTarget.Of("x86_64-apple-darwin")));
        }
    }

    /// <summary>
    ///     '@export var': C reads and writes the variable's storage by its C
    ///     name, and sees what Sable wrote, the initial value among it.
    /// </summary>
    [Fact]
    public void CReadsAndWritesAnExportedVariable() {
        if (!Programs.CanBuild) {
            return;
        }

        const string c = """
                         #include <stdint.h>
                         #include <stdbool.h>
                         extern int64_t sable_counter;
                         extern double sable_scale;
                         extern bool sable_ready;
                         extern const int32_t sable_limit;
                         int64_t c_bump(void) { sable_counter += 10; sable_ready = !sable_ready; return sable_counter * (int64_t)sable_scale + sable_limit; }
                         """;

        const string source = """
                              module Main

                              @export("sable_counter")
                              var counter: Int = 5

                              @export("c", "sable_scale")
                              var scale: F64 = 2.0

                              @export("sable_ready")
                              var ready = false

                              @export("sable_limit")
                              let limit: I32 = 7

                              @extern("c")
                              fn c_bump() -> Int

                              fn main() {
                                  counter = counter + 1
                                  print(c_bump())
                                  print("\(counter) \(ready)")
                                  scale = 3.0
                                  print(c_bump())
                              }
                              """;

        Assert.Equal("39\n16 true\n85\n", BuildWithC(source, c));
    }

    static bool Rosetta() {
        try {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/arch", ["-x86_64", "/usr/bin/true"]) { RedirectStandardError = true })!;
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        } catch (System.ComponentModel.Win32Exception) {
            return false;
        }
    }

    /// <summary>The program, with the C beside it, built for the target (this machine's by default) and run: what it prints.</summary>
    static string BuildWithC(string source, string c, NativeTarget? target = null) {
        var tree = SyntaxTree.Parse(new SourceText(source, "test.sb"), Pipeline.ParseOptionsFor(true, target ?? NativeTarget.Host));
        var result = Pipeline.Compile([tree], target: target);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var options = NativeOptions.Of(true, false) with { Lto = LtoMode.Off, Target = target };

        var directory = Directory.CreateTempSubdirectory("sable-interop-");
        try {
            var cSource = Path.Combine(directory.FullName, "side.c");
            File.WriteAllText(cSource, c);
            var cObject = Path.Combine(directory.FullName, "side.o");
            var compile = Process.Start(new ProcessStartInfo(NativeBuild.CompilerPath, [..target?.ClangFlags ?? [], "-O2", "-c", cSource, "-o", cObject]) { RedirectStandardError = true })!;
            var errors = compile.StandardError.ReadToEnd();
            compile.WaitForExit();
            Assert.True(compile.ExitCode == 0, errors);

            var program = Path.Combine(directory.FullName, "program.o");
            var built = NativeObjects.CompileIr(result.LlvmIr!, program, options);
            Assert.True(built.Succeeded, built.Output);
            var runtime = Path.Combine(directory.FullName, "runtime.o");
            Assert.True(NativeObjects.CompileRuntime(runtime, options).Succeeded);
            var executable = Platform.Executable(Path.Combine(directory.FullName, "program"));
            // C's maths is in libm on POSIX, in the C runtime itself on Windows.
            var linked = NativeObjects.Link([program, runtime, cObject, ..OperatingSystem.IsWindows() ? [] : new[] { "-lm" }], executable, options);
            Assert.True(linked.Succeeded, linked.Output);

            // Another architecture this Mac runs through Rosetta.
            var start = target is null || target.IsHost
                ? new ProcessStartInfo(executable)
                : new ProcessStartInfo("/usr/bin/arch", ["-" + target.Arch, executable]);
            start.RedirectStandardOutput = true;
            var run = Process.Start(start.Utf8())!;
            var output = run.StandardOutput.ReadToEnd();
            run.WaitForExit();
            Assert.Equal(0, run.ExitCode);
            return output;
        } finally {
            directory.Delete(true);
        }
    }
}
