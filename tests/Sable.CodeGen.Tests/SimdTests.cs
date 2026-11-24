using System.Diagnostics;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Portable SIMD (lib/Sable.Core/SIMD.sb): <c>SIMD&lt;T, N&gt;</c> is LLVM's
///     <c>&lt;N x T&gt;</c>, its operations vector instructions; the
///     interpreter computes each lane with the scalar builtin of the same
///     name, and the two are held to the same answers and the same traps.
/// </summary>
[Native]
public sealed class SimdTests {
    [Fact]
    public void FloatArithmeticIsLaneByLane() =>
        Assert.Equal(
            "<11.0, 22.0, 33.0, 44.0>\n110.0\n<0.5, 1.0, 1.5, 2.0>\n<1.0, 1.4142135, 1.7320508, 2.0>\n<-1.0, -2.0, -3.0, -4.0>\n<3.0, 5.0, 7.0, 9.0>\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let a = SIMD<F32, 4>([1, 2, 3, 4])
                        let b = SIMD<F32, 4>(repeating: 10)
                        let c = a * b + a
                        print(c)
                        print(c.reduceAdd())
                        print(a / SIMD<F32, 4>(repeating: 2))
                        print(a.squareRoot())
                        print(-a)
                        print(a.fusedMultiplyAdd(SIMD<F32, 4>(repeating: 2), SIMD<F32, 4>(repeating: 1)))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void EveryLaneTypeAndWidthComputes() =>
        AssertAgree(
            Program(
                """
                fn show<T: SIMDScalar, N: Int>(_ v: SIMD<T, N>) {
                    print("\(v) \(v.reduceAdd()) \(v.reduceMin()) \(v.reduceMax()) \(v.count)")
                }
                """,
                """
                    show(SIMD<I8, 16>.sequence(start: -8, step: 1))
                    show(SIMD<U8, 32>.sequence(start: 0, step: 3))
                    show(SIMD<I16, 8>.geometricSequence(start: 1, ratio: -3))
                    show(SIMD<U16, 4>.sequence(start: 7, step: 1000))
                    show(SIMD<I32, 64>.sequence(start: -100, step: 7))
                    show(SIMD<U32, 2>([4000000000, 1]))
                    show(SIMD<I64, 3>([-5, 9, 2]))
                    show(SIMD<U64, 8>.geometricSequence(start: 1, ratio: 10))
                    show(SIMD<Int, 4>.sequence(start: 100, step: -40))
                    show(SIMD<UInt, 2>([1, 2]))
                    show(SIMD<F64, 8>.sequence(start: 0.1, step: 0.2))
                    show(SIMD<F32, 3>([0.1, 0.2, 0.3]))
                    show(SIMD<F32, 16>.geometricSequence(start: 1.5, ratio: 1.1))
                    show(SIMD<F16, 8>.sequence(start: 0.5, step: 0.25))
                    show(SIMD<BF16, 4>.sequence(start: 1.5, step: 0.75))
                    let h = SIMD<F16, 4>([1, 2, 3, 4]) * SIMD<F16, 4>(repeating: 0.1)
                    print(h)
                    let bf = SIMD<BF16, 4>([1, 2, 3, 4]) / SIMD<BF16, 4>(repeating: 3)
                    print("\(bf) \(bf.squareRoot()) \(bf.fusedMultiplyAdd(bf, bf))")
                """
            )
        );

    [Fact]
    public void IntegerArithmeticTrapsAsTheScalarDoes() {
        var sums = Program("", """
                                   let a = SIMD<I32, 4>([1, 2, 2147483647, 4])
                                   print(a &+ SIMD<I32, 4>(repeating: 1))
                                   print(a.saturatingAdd(SIMD<I32, 4>(repeating: 1)))
                                   print(a + SIMD<I32, 4>(repeating: 1))
                               """);
        var run = AssertAgree(sums);
        Assert.Equal("<2, 3, -2147483648, 5>\n<2, 3, 2147483647, 5>\n", run.Stdout);
        Assert.Contains("integer overflow", run.Stderr, StringComparison.Ordinal);

        Assert.Contains("division by zero", AssertAgree(Program("", """
                                                                        let a = SIMD<U16, 4>([1, 2, 3, 4])
                                                                        print(a / SIMD<U16, 4>([1, 1, 1, 2]))
                                                                        print(a % SIMD<U16, 4>([3, 3, 3, 3]))
                                                                        print(a / SIMD<U16, 4>([1, 0, 1, 1]))
                                                                    """)).Stderr, StringComparison.Ordinal);
        Assert.Contains("integer overflow", AssertAgree(Program("", """
                                                                        let a = SIMD<I8, 2>([-128, 5])
                                                                        print(a / SIMD<I8, 2>([-1, 1]))
                                                                    """)).Stderr, StringComparison.Ordinal);
        Assert.Contains("shift out of range", AssertAgree(Program("", """
                                                                          let a = SIMD<U32, 2>([1, 2])
                                                                          print(a << SIMD<U32, 2>([31, 1]))
                                                                          print(a >> SIMD<U32, 2>([1, 32]))
                                                                      """)).Stderr, StringComparison.Ordinal);
        Assert.Contains("integer overflow", AssertAgree(Program("", """
                                                                        let a = SIMD<I64, 2>([-9223372036854775807, 3])
                                                                        print(a.abs())
                                                                        print(-a)
                                                                        print((a - SIMD<I64, 2>(repeating: 1)).abs())
                                                                    """)).Stderr, StringComparison.Ordinal);
        Assert.Contains("integer overflow", AssertAgree(Program("", """
                                                                        let a = SIMD<U8, 4>([16, 1, 2, 3])
                                                                        print(a &* a)
                                                                        print(a * a)
                                                                    """)).Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void BitsAndShiftsAreLaneByLane() =>
        Assert.Equal(
            "<8, 2, 0, 0> <14, 15, 15, 15> <6, 13, 15, 15> <-13, -4, -16, -9>\n<24, 12, 30, 16> <6, 0, 7, 4> <-1, -1, -1, 0>\n0 15 8\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let a = SIMD<I32, 4>([12, 3, 15, 8])
                        let b = SIMD<I32, 4>([10, 14, 0, 7])
                        print("\(a & b) \(a | b) \(a ^ b) \(~a)")
                        let s = SIMD<I32, 4>([1, 2, 1, 1])
                        print("\(a << s) \(a >> s) \(SIMD<I32, 4>([-1, -2, -3, 1]) >> SIMD<I32, 4>([1, 3, 2, 1]))")
                        print("\(a.reduceAnd()) \(a.reduceOr()) \(a.reduceXor())")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ComparisonsMakeMasksThatSelect() =>
        Assert.Equal(
            "<false, false, true, true> 2 true false\n<1, 2, 30, 40>\n<10, 20, 3, 4>\n<true, true, false, false> true\nSIMDMask<4>(true, false, true, false) <false, true, false, true>\n<5, 2, 5, 4>\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let a = SIMD<I32, 4>([1, 2, 3, 4])
                        let b = SIMD<I32, 4>([10, 20, 30, 40])
                        let big = a.lanesGreater(than: SIMD<I32, 4>(repeating: 2))
                        print("\(big) \(big.trueCount) \(big.anyTrue) \(big.allTrue)")
                        print(SIMD<I32, 4>.select(big, b, a))
                        print(a.replacing(with: b, where: ~big))
                        let le = a.lanesLessOrEqual(to: SIMD<I32, 4>(repeating: 2))
                        print("\(le) \(le == ~big)")
                        let alternate = SIMDMask<4>([true, false, true, false])
                        print("\(alternate.debugDescription()) \(alternate ^ SIMDMask<4>(repeating: true))")
                        print(a.max(SIMD<I32, 4>([5, 0, 5, 0])).min(SIMD<I32, 4>([9, 2, 9, 4])))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void FloatComparisonsFollowIeee() =>
        Assert.Equal(
            "<false, true, true> <true, false, false> false true\n<nan, 1.0, -0.0> nan\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let nan = F64.nan
                        let a = SIMD<F64, 3>([nan, 1.0, -0.0])
                        let b = SIMD<F64, 3>([nan, 1.0, 0.0])
                        print("\(a.lanesLess(than: b) | a.lanesEqual(b)) \(a.lanesNotEqual(b) | a.lanesLess(than: b)) \(a == a) \(SIMD<F64, 3>([1, 2, 3]) == SIMD<F64, 3>([1, 2, 3]))")
                        let m = SIMD<F64, 3>([1.0, 5.0, -0.0]).minimum(a)
                        print("\(m) \(m.reduceAdd())")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void LanesMoveAbout() =>
        Assert.Equal(
            "<40, 30, 20, 10> <20, 20>\n<1, 2, 3, 4> <1, 2> <3, 4>\n<0, 10, 1, 11> <2, 12, 3, 13>\n<0, 1, 2, 3> <10, 11, 12, 13>\n<7, 2, 3, 4> 7\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let v = SIMD<I32, 4>([10, 20, 30, 40])
                        print("\(v.swizzled(SIMD<Int, 4>([3, 2, 1, 0]))) \(v.swizzled(SIMD<Int, 2>([1, 1])))")
                        let whole = SIMD<I32, 4>.concat(SIMD<I32, 2>([1, 2]), SIMD<I32, 2>([3, 4]))
                        print("\(whole) \(SIMD<I32, 2>.lowHalf(of: whole)) \(SIMD<I32, 2>.highHalf(of: whole))")
                        let (low, high) = SIMD<I32, 4>.zip(SIMD<I32, 4>([0, 1, 2, 3]), SIMD<I32, 4>([10, 11, 12, 13]))
                        print("\(low) \(high)")
                        let (even, odd) = SIMD<I32, 4>.unzip(low, high)
                        print("\(even) \(odd)")
                        var w = whole
                        w[0] = 7
                        print("\(w) \(w[0])")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ALaneOutsideTheVectorTraps() {
        Assert.Contains("SIMD lane index out of range", AssertAgree(Program("", """
                                                                                    let v = SIMD<I32, 4>(repeating: 1)
                                                                                    print(v[3])
                                                                                    print(v[4])
                                                                                """)).Stderr, StringComparison.Ordinal);
        Assert.Contains("SIMD lane index out of range", AssertAgree(Program("", """
                                                                                    let v = SIMD<I32, 4>(repeating: 1)
                                                                                    print(v.swizzled(SIMD<Int, 2>([0, -1])))
                                                                                """)).Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorsLoadAndStoreArrays() {
        var run = AssertAgree(Program("", """
                                              let values: F32[>..] = [1, 2, 3, 4, 5, 6]
                                              let v = SIMD<F32, 4>.load(from: values, at: 2)
                                              print(v)
                                              var target: F32[>..] = [0, 0, 0, 0, 0]
                                              let copy = target
                                              target.store(v * SIMD<F32, 4>(repeating: 2), at: 1)
                                              print("\(target) \(copy)")
                                              var table: I32[>..] = [10, 20, 30, 40]
                                              let g = SIMD<I32, 4>.gather(from: table, at: SIMD<Int, 4>([3, 0, 2, 2]))
                                              print(g)
                                              table.scatter(SIMD<I32, 2>([7, 9]), at: SIMD<Int, 2>([3, 0]))
                                              print(table)
                                              print(SIMD<F32, 4>(values[1..<5]))
                                              print(SIMD<F32, 4>.load(from: values, at: 3))
                                          """));
        Assert.Equal("<3.0, 4.0, 5.0, 6.0>\n[0.0, 6.0, 8.0, 10.0, 12.0] [0.0, 0.0, 0.0, 0.0, 0.0]\n<40, 10, 30, 30>\n[9, 20, 30, 7]\n<2.0, 3.0, 4.0, 5.0>\n", run.Stdout);
        Assert.Contains("array index out of range", run.Stderr, StringComparison.Ordinal);

        Assert.Contains("array index out of range", AssertAgree(Program("", """
                                                                                var target: I64[>..] = [0, 0, 0]
                                                                                target.store(SIMD<I64, 2>(repeating: 1), at: 1)
                                                                                print(target)
                                                                                target.store(SIMD<I64, 2>(repeating: 1), at: -1)
                                                                            """)).Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void LanesConvertAsUncheckedAsDoes() =>
        Assert.Equal(
            "<1, -2, 2147483647, 0>\n<1.0, -1.0>\n<44, 1>\n<255, 0, 1, 0>\n<1.5, -2.5>\n<3.0, 1.8446744073709552e+19>\n<1.0, 0.5> <1.0, 0.5>\n",
            AssertAgree(
                Program(
                    "",
                    """
                        var floats = SIMD<F32, 4>([1.5, -2.5, 1e10, 0.0])
                        floats[3] = F32.nan
                        print(SIMD<I32, 4>.converting(floats))
                        print(SIMD<F64, 2>.converting(SIMD<I32, 2>([1, -1])))
                        print(SIMD<U8, 2>.converting(SIMD<I32, 2>([300, 257])))
                        print(SIMD<U8, 4>.converting(SIMD<F64, 4>([1000.0, -5.0, 1.9, -0.9])))
                        print(SIMD<F32, 2>.converting(SIMD<F64, 2>([1.5, -2.5])))
                        var big = SIMD<U64, 2>(repeating: 3)
                        big[1] = U64.max
                        print(SIMD<F64, 2>.converting(big))
                        print("\(SIMD<F64, 2>.converting(SIMD<BF16, 2>([1.0, 0.5]))) \(SIMD<BF16, 2>.converting(SIMD<F16, 2>([1.0, 0.5])))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void VectorsAreValuesInStructsArraysAndOptionals() =>
        Assert.Equal(
            "<1.0, 2.0> <3.0, 4.0> true\n<4.0, 6.0>\n2\n<2, 2, 2, 2, 2, 2, 2, 2>\n",
            AssertAgree(
                Program(
                    """
                    struct Particle {
                        var position: SIMD<F64, 2>
                        var velocity: SIMD<F64, 2>
                    }
                    """,
                    """
                        var p = Particle(position: SIMD<F64, 2>([1, 2]), velocity: SIMD<F64, 2>([3, 4]))
                        let q = p
                        p.position = p.position + p.velocity
                        print("\(q.position) \(q.velocity) \(q.position != p.position)")
                        var many: SIMD<F64, 2>[>..] = [p.position]
                        many.append(q.velocity)
                        print(many[0])
                        let maybe: SIMD<I16, 8>? = SIMD<I16, 8>(repeating: 2)
                        var seen: [SIMD<I16, 8>: Int] = [:]
                        seen[maybe!] = 1
                        seen[SIMD<I16, 8>(repeating: 3)] = 2
                        print(seen.count)
                        if let v = maybe {
                            print(v)
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void PreferredWidthIsTheTargetsVectorRegister() =>
        Assert.Equal(
            "4 2 16 2 8\n",
            AssertAgree(Program("", "    print(\"\\(F32.preferredVectorWidth) \\(F64.preferredVectorWidth) \\(U8.preferredVectorWidth) \\(Int.preferredVectorWidth) \\(F16.preferredVectorWidth)\")")).Stdout
        );

    [Fact]
    public void AWrongLaneCountTraps() =>
        Assert.Contains("a SIMD vector of 4 lanes made from 3", AssertAgree(Program("", "    print(SIMD<I32, 4>([1, 2, 3]))")).Stderr, StringComparison.Ordinal);

    /// <summary>The program's LLVM IR, before LLVM's passes, and after clang's -O2.</summary>
    static (string Unoptimised, string Optimised) Ir(string source) {
        var result = Pipeline.Compile(new SourceText(source, "test.sb"), optimised: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var directory = Directory.CreateTempSubdirectory("sable-simd-");
        try {
            var input = Path.Combine(directory.FullName, "in.ll");
            var output = Path.Combine(directory.FullName, "out.ll");
            File.WriteAllText(input, result.LlvmIr!);
            var start = new ProcessStartInfo(NativeBuild.CompilerPath) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-O2", "-S", "-emit-llvm", "-Wno-override-module", input, "-o", output }) {
                start.ArgumentList.Add(argument);
            }

            var run = ChildProcess.Run(start, ChildProcess.ProgramLimit, "");
            Assert.True(run.ExitCode == 0, run.Stderr);
            return (result.LlvmIr!, File.ReadAllText(output));
        } finally {
            directory.Delete(true);
        }
    }

    const string Kernel = """
                          @inline(never)
                          fn axpy(_ a: F32, _ x: F32[:], _ y: inout F32[>..]) {
                              let scale = SIMD<F32, 4>(repeating: a)
                              var i = 0
                              while i + 4 <= x.count {
                                  let v = SIMD<F32, 4>.load(from: x, at: i) * scale + SIMD<F32, 4>.load(from: y, at: i)
                                  y.store(v, at: i)
                                  i += 4
                              }
                          }

                          @inline(never)
                          fn total(_ x: F64[:]) -> F64 {
                              var sum = SIMD<F64, 2>()
                              var i = 0
                              while i + 2 <= x.count {
                                  sum = sum + SIMD<F64, 2>.load(from: x, at: i)
                                  i += 2
                              }
                              return sum.reduceAdd()
                          }
                          """;

    [Fact]
    public void ALoopOverVectorsIsVectorInstructions() {
        if (!CanBuild) {
            return;
        }

        // Arrays made at run time, so that LLVM cannot fold the loops away.
        var (unoptimised, optimised) = Ir(Program(Kernel, """
                                                              var x: F32[>..] = []
                                                              var y: F32[>..] = []
                                                              var z: F64[>..] = []
                                                              for i in 0..<Runtime.argumentCount() * 8 {
                                                                  x.append(F32(i))
                                                                  y.append(1)
                                                                  z.append(F64(i))
                                                              }
                                                              axpy(2, x, &y)
                                                              print(y)
                                                              print(total(z))
                                                          """));

        // As Sable lowers it, the SIMD type is LLVM's vector type, its operations vector instructions …
        Assert.Contains("load <4 x float>, ptr", unoptimised, StringComparison.Ordinal);
        Assert.Contains("fmul <4 x float>", unoptimised, StringComparison.Ordinal);
        Assert.Contains("fadd <2 x double>", unoptimised, StringComparison.Ordinal);
        Assert.Contains("store <4 x float>", unoptimised, StringComparison.Ordinal);

        // … and they stay vector instructions through LLVM's -O2, inside the loops.
        Assert.Matches(@"fmul (fast )?<4 x float>", optimised);
        Assert.Matches(@"fadd (fast )?<4 x float>", optimised);
        Assert.Matches(@"fadd (fast )?<2 x double>", optimised);
        Assert.Contains("store <4 x float>", optimised, StringComparison.Ordinal);
    }

    /// <summary>Sable.Math's kernels, at lengths with a vector body and a scalar tail, and with a tail alone.</summary>
    [Fact]
    public void TheVectorKernelsAgree() =>
        Assert.Equal(
            "703.0 15466.0 1.00 0.0 36.0\n36.0 -36.0 12.0 0.25 1.00\n5.0 650.0 78.0 3.0\n",
            AssertAgree(
                Program(
                    """
                    import Sable.Math
                    fn ramp(_ count: Int) -> F32[>..] {
                        var values: F32[>..] = []
                        for i in 0..<count {
                            values.append(F32(i))
                        }
                        return values
                    }
                    fn rampWide(_ count: Int) -> F64[>..] {
                        var values: F64[>..] = []
                        for i in 0..<count {
                            values.append(F64(i))
                        }
                        return values
                    }
                    """,
                    """
                        let x = ramp(38)
                        let y = ramp(38)
                        print("\(TensorPrimitives.sum(x)) \(TensorPrimitives.dot(x, y) - TensorPrimitives.dot(ramp(19), ramp(19))) \(TensorPrimitives.cosineSimilarity(x, ramp(38)):.2f) \(TensorPrimitives.min(x)) \(TensorPrimitives.max(ramp(37)))")
                        var result = ramp(38)
                        TensorPrimitives.add(x, y, into: &result)
                        let a = result[18]
                        TensorPrimitives.subtract(x, y, into: &result)
                        let b = result[18] - a
                        TensorPrimitives.multiply(ramp(4), ramp(4), into: &result)
                        let c = result[2] + result[3] - 1
                        TensorPrimitives.divide(ramp(38), y, into: &result)
                        var probabilities = ramp(20)
                        TensorPrimitives.softmax(ramp(20), into: &probabilities)
                        print("\(a) \(b) \(c) \(result[2] / 4) \(F64(TensorPrimitives.sum(probabilities)):.2f)")
                        let wide = rampWide(13)
                        let side: F64[>..] = [3, 4]
                        print("\(TensorPrimitives.norm(side)) \(TensorPrimitives.dot(wide, wide)) \(TensorPrimitives.sum(wide)) \(TensorPrimitives.max(rampWide(4)))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AMasksBitsAreItsLanes() =>
        Assert.Equal(
            "5 2 0 2\n4 3\n9223372036854775809 2 0 63\n4294967295 32 0 31\n0 0 64 -1\n0b1010101010101010\n",
            AssertAgree(
                Program(
                    """
                    fn show<N: Int>(_ m: SIMDMask<N>) {
                        print("\(m.bits) \(m.trueCount) \(m.firstTrue) \(m.lastTrue)")
                    }
                    """,
                    """
                        show(SIMDMask<3>([true, false, true]))
                        let two = SIMDMask<4>([false, false, true, true])
                        print("\(two.bits >> 2 << 2 & 7) \(two.lastTrue)")
                        var wide = SIMDMask<64>()
                        wide[0] = true
                        wide[63] = true
                        show(wide)
                        show(SIMDMask<32>(repeating: true))
                        show(SIMDMask<16>())
                        let odd = SIMD<U8, 16>.sequence(start: 0, step: 1) & SIMD<U8, 16>(repeating: 1)
                        print("\(odd.lanesEqual(SIMD<U8, 16>(repeating: 1)).bits:#b)")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ByteIndicesPickLanes() =>
        Assert.Equal(
            "<30, 10, 30, 20>\n<13, 14, 15, 16, 1, 2, 3, 4>\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print(SIMD<I32, 4>([10, 20, 30, 40]).swizzled(SIMD<U8, 4>([2, 0, 2, 1])))
                        print(SIMD<U8, 16>.sequence(start: 1, step: 1).swizzled(SIMD<U8, 8>([12, 13, 14, 15, 0, 1, 2, 3])))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void BytesAreLookedUpAndLanesReinterpreted() =>
        AssertAgree(
            Program(
                """
                fn look<N: Int>(_ table: SIMD<U8, N>) {
                    for start in [0, 7, 16, 40, 60, 120, 240] as Int[>..] {
                        print(table.lookup(SIMD<U8, 16>.sequence(start: U8(start), step: 1)))
                    }
                    print(table.lookup(SIMD<U8, 32>.sequence(start: 3, step: 2)))
                    print(table.lookup(SIMD<U8, 4>([0, 1, 200, 2])))
                }
                """,
                """
                    look(SIMD<U8, 4>([9, 8, 7, 6]))
                    look(SIMD<U8, 16>.sequence(start: 100, step: 3))
                    look(SIMD<U8, 32>.sequence(start: 100, step: 3))
                    look(SIMD<U8, 64>.sequence(start: 100, step: 2))
                    let bytes = SIMD<U8, 16>([1, 2, 3, 4, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0x80, 0x3F, 0, 0, 0x20, 0xC1])
                    print(SIMD<I32, 4>.reinterpreting(bytes))
                    print(SIMD<F32, 4>.reinterpreting(bytes)[2])
                    print(SIMD<F16, 8>.reinterpreting(bytes)[5])
                    print(SIMD<BF16, 8>.reinterpreting(bytes)[7])
                    print(SIMD<U64, 2>.reinterpreting(bytes))
                    print(SIMD<U8, 16>.reinterpreting(SIMD<F64, 2>([1.5, -0.0])))
                    print(SIMD<I8, 4>.reinterpreting(SIMD<I16, 2>([-2, 300])))
                    print(SIMD<U16, 2>.reinterpreting(SIMD<BF16, 2>([1.0, -2.0])))
                """
            )
        );

    /// <summary>A loop of stores makes the array the only reference to its buffer once, and a shared one is still left as it was.</summary>
    [Fact]
    public void ALoopOfStoresLeavesASharedArrayAsItWas() =>
        Assert.Equal(
            "[1, 1, 1, 1, 1, 1, 1, 1, 1]\n[2, 2, 2, 2, 2, 2, 2, 2, 1]\n[3, 3, 3, 3, 3, 3, 3, 3, 1]\n[1, 1, 1, 1, 1, 1, 1, 1, 1]\n",
            AssertAgree(
                Program(
                    """
                    fn fillUp(_ xs: inout I32[>..], _ value: I32) {
                        var i = 0
                        while i + 4 <= xs.count {
                            xs.store(SIMD<I32, 4>(repeating: value), at: i)
                            i += 4
                        }
                    }
                    extension I32[:] {
                        mutating fn addOne() {
                            var i = 0
                            while i + 4 <= self.count {
                                self.store(SIMD<I32, 4>.load(from: self, at: i) &+ SIMD<I32, 4>(repeating: 1), at: i)
                                i += 4
                            }
                        }
                    }
                    """,
                    """
                        var a = I32[>..](repeating: 1, count: 9)
                        let held = a
                        fillUp(&a, 2)
                        print(held)
                        print(a)
                        let shared = a
                        a.addOne()
                        print(a)
                        let _ = shared
                        print(held)
                    """
                )
            ).Stdout
        );

    /// <summary>An array's sum traps where one along the way does not fit, though the whole would: as its loop does.</summary>
    [Theory]
    [InlineData("Int", "9223372036854775807", "-1")]
    [InlineData("I64", "-9223372036854775808", "1")]
    [InlineData("I32", "2147483647", "-1")]
    [InlineData("U64", "18446744073709551615", "0")]
    [InlineData("U32", "4294967295", "0")]
    public void AnArraysSumTrapsWhereALoopsWould(string type, string extreme, string after) {
        // Zeros, then the bound, then as much as undoes it: fits, the one
        // before the other; the other way about, one more than fits.
        string Sum(string last) =>
            Program(
                "",
                $$"""
                    var xs = {{type}}[>..](repeating: 0, count: 70)
                    xs[20] = {{extreme}}
                    xs[21] = {{after}}
                    xs[60] = {{last}}
                    print(xs.sum())
                """
            );

        Assert.Equal(0, AssertAgree(Sum("0")).ExitCode);
        var trapped = AssertAgree(
            Program(
                "",
                $$"""
                    var xs = {{type}}[>..](repeating: 0, count: 70)
                    xs[20] = {{extreme}}
                    xs[33] = {{(extreme.StartsWith('-') ? "-1" : "1")}}
                    xs[34] = {{(type.StartsWith('U') ? "0" : extreme.StartsWith('-') ? "1" : "-1")}}
                    print(xs.sum())
                """
            )
        );
        Assert.NotEqual(0, trapped.ExitCode);
        Assert.Contains("overflow", trapped.Stderr);
    }

    [Fact]
    public void AByteIndexOutsideTheVectorTraps() {
        var trapped = AssertAgree(Program("", "print(SIMD<I32, 4>([1, 2, 3, 4]).swizzled(SIMD<U8, 2>([0, 4])))"));
        Assert.NotEqual(0, trapped.ExitCode);
        Assert.Contains("SIMD lane index out of range", trapped.Stderr);
    }

    /// <summary>Array+VectorSearch.sb's searches and the comparison by bits: at lengths about a step's, a match at each end and in the middle.</summary>
    [Fact]
    public void TheArraySearchesAgree() =>
        AssertAgree(
            Program(
                """
                fn probe<T: SIMDScalar>(_ other: T, _ wanted: T) {
                    for n in [0, 1, 7, 8, 9, 31, 32, 33, 70] as Int[>..] {
                        var xs = T[>..](repeating: other, count: n)
                        let all = xs
                        for at in [0, n / 2, n - 1] as Int[>..] {
                            if at < 0 || at >= n {
                                continue
                            }
                            xs[at] = wanted
                            print("\(n) \(at): \(xs.firstIndex(of: wanted) ?? -1) \(xs.lastIndex(of: wanted) ?? -1) \(xs.count(of: wanted)) \(xs.contains(wanted)) \(xs.firstIndex(ofAny: [wanted, wanted]) ?? -1) \(xs == all) \(xs < all) \(xs.commonPrefixLength(with: all))")
                        }
                        print("\(n): \(all.contains(wanted)) \(all.lastIndex(of: wanted) ?? -1) \(all == all[..<n])")
                    }
                }
                """,
                """
                    probe(U8(1), U8(200))
                    probe(I16(1), I16(-2))
                    probe(I32(1), I32(-2))
                    probe(1, 2)
                    probe(1.0, 0.5)
                """
            )
        );

    [Fact]
    public void TheKernelLoopAgrees() =>
        Assert.Equal(
            "[3.0, 5.0, 7.0, 9.0, 11.0, 13.0, 15.0, 17.0]\n10.0\n",
            AssertAgree(Program(Kernel, """
                                            var y: F32[>..] = [1, 1, 1, 1, 1, 1, 1, 1]
                                            axpy(2, [1, 2, 3, 4, 5, 6, 7, 8], &y)
                                            print(y)
                                            print(total([1, 2, 3, 4]))
                                        """)).Stdout
        );

    [Fact]
    public void AConstantGivesTheLaneCount() =>
        Assert.Equal(
            "4\n<2.0, 2.0, 2.0, 2.0>\n32.0\n4.0\n16\n",
            AssertAgree(
                Program(
                    """
                    const f32Lanes = Platform.vectorBytes / 4
                    const wide = f32Lanes * 4

                    struct Holder {
                        const lanes = 8
                    }

                    fn count<N: Int>(_ v: SIMD<F32, N>) -> Int { return N }
                    """,
                    """
                        let a = SIMD<F32, f32Lanes>(repeating: 2)
                        print(count(a))
                        print(a)
                        let b: SIMD<F32, wide> = SIMD(repeating: 2)
                        print(b.reduceAdd())
                        print(SIMD<F64, Holder.lanes>(repeating: 0.5).reduceAdd())
                        print(SIMD<U8, Platform.vectorBytes>(repeating: 1).reduceAdd())
                    """
                )
            ).Stdout
        );
}
