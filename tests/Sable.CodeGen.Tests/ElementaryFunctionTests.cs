using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A float's elementary functions (<c>sin</c>, <c>exp</c>, <c>pow</c>, …),
///     its bits, and an integer's bytes swapped: native code calls libm (or
///     LLVM's intrinsic, which is libm too), the interpreter calls the same
///     libm, and the two must print the same digits — the shortest that
///     round-trip, so the same bits.
/// </summary>
public sealed class ElementaryFunctionTests {
    const string Specials = """
                                let inf = F64.fromBits(0x7FF0_0000_0000_0000)
                                let xs: F64[>..] = [0.0, -0.0, 0.5, -0.5, 1.0, -1.0, 2.5, -3.75, 100.0, 1e-300, 1e300, inf, -inf, inf - inf]

                            """;

    [Fact]
    public void EveryUnaryFunctionAgreesWithLibmOnSpecialValues() {
        var execution = AssertAgree(
            Program(
                "",
                Specials + """
                               for x in xs {
                                   print("\(x): \(x.squareRoot()) \(x.cbrt()) \(x.sin()) \(x.cos()) \(x.tan()) \(x.asin()) \(x.acos()) \(x.atan())")
                                   print("  \(x.sinh()) \(x.cosh()) \(x.tanh()) \(x.asinh()) \(x.acosh()) \(x.atanh())")
                                   print("  \(x.exp()) \(x.exp2()) \(x.expm1()) \(x.log()) \(x.log2()) \(x.log10()) \(x.log1p())")
                                   print("  \(x.gamma()) \(x.logGamma()) \(x.erf()) \(x.erfc())")
                                   let f = F32(x)
                                   print("  \(f.squareRoot()) \(f.sin()) \(f.cos()) \(f.tan()) \(f.exp()) \(f.log()) \(f.cbrt()) \(f.erf()) \(f.gamma())")
                               }
                           """
            )
        );
        Assert.Contains("1.0: 1.0 1.0 0.8414709848078965 0.5403023058681398", execution.Stdout, StringComparison.Ordinal);
        Assert.Contains("-0.0: -0.0 -0.0 -0.0 1.0", execution.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBinaryFunctionAgreesWithLibmOnSpecialValues() =>
        AssertAgree(
            Program(
                "",
                Specials + """
                               for y in xs {
                                   for x in xs {
                                       print("\(y) \(x): \(y.atan2(x)) \(y.pow(x)) \(y.hypot(x)) \(F32(y).pow(F32(x))) \(F32(y).atan2(F32(x)))")
                                   }
                               }
                           """
            )
        );

    [Fact]
    public void TheFunctionsGiveLibmsKnownValues() =>
        Assert.Equal(
            "1.4142135623730951\n1.0\n0.0\n1024.0\n24.0\n-2.0\n3.141592653589793\n5.0\n-0.0\n1.0\n-inf\nnan\n1.0\n1e-10\n1e-10\n1.4142135\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print(2.0.squareRoot())
                        print(0.0.exp())
                        print(1.0.log())
                        print(2.0.pow(10.0))
                        print(5.0.gamma())
                        print((-8.0).cbrt())
                        print(0.0.atan2(-1.0))
                        print(3.0.hypot(4.0))
                        print((-0.0).sin())
                        print(F64.fromBits(0x7FF0_0000_0000_0000).erf())
                        print(0.0.log())
                        print((-1.0).log())
                        print((0.0 / 0.0).pow(0.0))
                        print(1e-10.log1p() + 5e-21)
                        print(1e-10.expm1() - 5e-21)
                        let two: F32 = 2.0
                        print(two.squareRoot())
                    """
                )
            ).Stdout
        );

    [Fact]
    public void BitsAndByteSwapsAgree() =>
        Assert.Equal(
            "4607182418800017408 2.0 1065353216 1.0 15360 1.0\n-9223372036854775808 -0.0\n"
            + "1144201745 13330 772 -1 -6124895493223874560 18374686479671623680 290 0x0100\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let half: F16 = 1.0
                        print("\(1.0.bits) \(F64.fromBits(0x4000_0000_0000_0000)) \(F32(1.0).bits) \(F32.fromBits(0x3F80_0000)) \(half.bits) \(F16.fromBits(0x3C00))")
                        print("\((-0.0).bits unchecked as I64) \(F64.fromBits(0x8000_0000_0000_0000))")
                        let a: U32 = 0x11223344
                        let b: U16 = 0x1234
                        let c: I16 = 0x0403
                        let d: I32 = -1
                        let e: Int = 0xAB
                        let f: U64 = 0xFF
                        let g: U128 = 0x2201
                        let h: I128 = 0x0001
                        print("\(a.byteSwapped) \(b.byteSwapped) \(c.byteSwapped) \(d.byteSwapped) \(e.byteSwapped) \(f.byteSwapped) \(g.byteSwapped >> 112) \(h.byteSwapped == I128(1) << 120 ? "0x0100" : "wrong")")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ElementaryFunctionsLowerToIntrinsicsOrLibm() {
        var result = Pipeline.Compile(
            new SourceText(
                Program(
                    "",
                    """
                        let x = F64(Runtime.argumentCount())
                        let f = F32(x)
                        print(x.sin() + x.cbrt() + x.pow(x) + x.atan2(x))
                        print(f.sin() + f.tan())
                        let u = U32(x)
                        print(u.byteSwapped)
                        print(x.bits)
                    """
                ),
                "test.sb"
            )
        );
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var ir = result.LlvmIr!;
        Assert.Contains("call double @llvm.sin.f64(double", ir, StringComparison.Ordinal);
        Assert.Contains("call double @llvm.pow.f64(double", ir, StringComparison.Ordinal);
        Assert.Contains("call double @cbrt(double", ir, StringComparison.Ordinal);
        Assert.Contains("call double @atan2(double", ir, StringComparison.Ordinal);
        Assert.Contains("call float @llvm.sin.f32(float", ir, StringComparison.Ordinal);
        Assert.Contains("call float @tanf(float", ir, StringComparison.Ordinal);
        Assert.Contains("call i32 @llvm.bswap.i32(i32", ir, StringComparison.Ordinal);
        Assert.Contains("bitcast double", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void ACFunctionOfTheSameNameIsDeclaredOnce() {
        // The interpreter calls no '@extern' function, so this is native alone.
        if (!CanBuild) {
            return;
        }

        Assert.Equal(
            "2.0\n2.0\n",
            BuildAndRun(
                Program(
                    "@extern(\"c\") fn cbrt(_ x: F64) -> F64",
                    """
                        print(cbrt(8.0))
                        print(8.0.cbrt())
                    """
                )
            ).Stdout
        );
    }
}
