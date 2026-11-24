using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A float's elementary functions (Sable.Math's <c>sin</c>, <c>exp</c>,
///     <c>pow</c>, …, generic over <c>ElementaryFunctions</c>), its bits,
///     and an integer's bytes swapped: native code calls libm (or LLVM's
///     intrinsic, which is libm too), the interpreter calls the same libm,
///     and the two must print the same digits — the shortest that
///     round-trip, so the same bits.
/// </summary>
[Native]
public sealed class ElementaryFunctionTests {
    const string Specials = """
                                let inf = F64.fromBits(0x7FF0_0000_0000_0000)
                                let xs: F64[>..] = [0.0, -0.0, 0.5, -0.5, 1.0, -1.0, 2.5, -3.75, 100.0, 1e-300, 1e300, inf, -inf, inf - inf]

                            """;

    [Fact]
    public void EveryUnaryFunctionAgreesWithLibmOnSpecialValues() {
        var execution = AssertAgree(
            Program(
                "import Sable.Math",
                Specials + """
                               for x in xs {
                                   print("\(x): \(x.squareRoot()) \(cbrt(x)) \(sin(x)) \(cos(x)) \(tan(x)) \(asin(x)) \(acos(x)) \(atan(x))")
                                   print("  \(sinh(x)) \(cosh(x)) \(tanh(x)) \(asinh(x)) \(acosh(x)) \(atanh(x))")
                                   print("  \(exp(x)) \(exp2(x)) \(expm1(x)) \(log(x)) \(log2(x)) \(log10(x)) \(log1p(x))")
                                   print("  \(gamma(x)) \(logGamma(x)) \(erf(x)) \(erfc(x))")
                                   let f = F32(x)
                                   print("  \(f.squareRoot()) \(sin(f)) \(cos(f)) \(tan(f)) \(exp(f)) \(log(f)) \(cbrt(f)) \(erf(f)) \(gamma(f))")
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
                "import Sable.Math",
                Specials + """
                               for y in xs {
                                   for x in xs {
                                       print("\(y) \(x): \(atan2(y, x)) \(pow(y, x)) \(hypot(y, x)) \(pow(F32(y), F32(x))) \(atan2(F32(y), F32(x)))")
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
                    "import Sable.Math",
                    """
                        print(2.0.squareRoot())
                        print(exp(0.0))
                        print(log(1.0))
                        print(pow(2.0, 10.0))
                        print(gamma(5.0))
                        print(cbrt(-8.0))
                        print(atan2(0.0, -1.0))
                        print(hypot(3.0, 4.0))
                        print(sin(-0.0))
                        print(erf(F64.fromBits(0x7FF0_0000_0000_0000)))
                        print(log(0.0))
                        print(log(-1.0))
                        print(pow(0.0 / 0.0, 0.0))
                        print(log1p(1e-10) + 5e-21)
                        print(expm1(1e-10) - 5e-21)
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
    public void ElementaryFunctionsLowerToLibmCallsLLVMMayNotFold() {
        var result = Pipeline.Compile(
            new SourceText(
                Program(
                    "import Sable.Math",
                    """
                        let x = F64(Runtime.argumentCount())
                        let f = F32(x)
                        print(sin(x) + cbrt(x) + pow(x, x) + atan2(x, x))
                        print(sin(f) + tan(f))
                        let u = U32(x)
                        print(u.byteSwapped)
                        print(x.bits)
                    """
                ),
                "test.sb"
            ),
            optimised: true
        );
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var ir = result.LlvmIr!;
        Assert.Contains("call double @sin(double", ir, StringComparison.Ordinal);
        Assert.Contains("call double @pow(double", ir, StringComparison.Ordinal);
        Assert.Contains("call double @cbrt(double", ir, StringComparison.Ordinal);
        Assert.Contains("call double @atan2(double", ir, StringComparison.Ordinal);
        Assert.Contains("call float @sinf(float", ir, StringComparison.Ordinal);
        Assert.Contains("call float @tanf(float", ir, StringComparison.Ordinal);
        Assert.Matches(@"call float @tanf\(float [^)]*\) nobuiltin", ir);
        Assert.DoesNotContain("@llvm.sin.", ir, StringComparison.Ordinal);
        Assert.Contains("call i32 @llvm.bswap.i32(i32", ir, StringComparison.Ordinal);
        Assert.Contains("bitcast double", ir, StringComparison.Ordinal);

        // Optimised, the generic functions are monomorphised and inlined to
        // the call itself: no function of Sable.Math's is called for them.
        Assert.DoesNotContain("@\"sable.Sable.Math.", ir, StringComparison.Ordinal);
    }

    static readonly string[] Unary = [
        "cbrt", "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh", "asinh", "acosh", "atanh",
        "exp", "exp2", "expm1", "log", "log2", "log10", "log1p", "gamma", "logGamma", "erf", "erfc",
    ];

    static readonly string[] Binary = ["atan2", "pow", "hypot"];

    static readonly string[] Operands = ["0.0", "-0.0", "0.1", "-0.5", "0.7", "1.0", "-1.0", "1.5", "2.5", "-3.75", "10.0", "100.0"];

    /// <summary>
    ///     Every function of the table, of F64 and of F32, given constants
    ///     and the same operands known only at run time: each line's two
    ///     answers are libm's bits, the same, and the interpreter, an
    ///     optimised build and an unoptimised one print the same lines. LLVM
    ///     folded a constant call with its own arithmetic — an F32 one in F64,
    ///     rounded — so macOS's <c>atan2f(-0, -1)</c>, the F32 below pi, was
    ///     the one above it where the operands were constants.
    /// </summary>
    [Fact]
    public void ConstantsAndRunTimeValuesGiveLibmsBitsAtEveryLevel() {
        var body = new System.Text.StringBuilder("    let one: F64 = Runtime.argumentCount() < 100 ? 1.0 : 2.0\n    let narrow = F32(one)\n");
        foreach (var type in new[] { "F64", "F32" }) {
            var unit = type == "F64" ? "one" : "narrow";
            foreach (var function in Unary) {
                foreach (var x in Operands) {
                    body.Append($"    print(\"{type} {function}({x}) \\({function}({x} as {type}).bits) \\({function}(({x} as {type}) * {unit}).bits)\")\n");
                }
            }

            foreach (var function in Binary) {
                foreach (var y in Operands) {
                    foreach (var x in new[] { "-1.0", "0.5", "-0.0", "2.0" }) {
                        body.Append($"    print(\"{type} {function}({y}, {x}) \\({function}({y} as {type}, {x}) .bits) \\({function}(({y} as {type}) * {unit}, ({x} as {type}) * {unit}).bits)\")\n");
                    }
                }
            }
        }

        var program = Program("import Sable.Math", body.ToString().Replace(") .bits", ").bits", StringComparison.Ordinal));
        var interpreted = AssertAgree(program).Stdout;
        foreach (var line in interpreted.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = line.Split(' ');
            Assert.True(parts[^1] == parts[^2], $"a constant and a value known at run time differ: {line}");
        }

        Assert.Contains("F32 atan2(-0.0, -1.0) ", interpreted, StringComparison.Ordinal);
        if (!CanBuild) {
            return;
        }

        Assert.Equal(interpreted, BuildAndRun(program).Stdout);
        Assert.Equal(interpreted, BuildAndRun(program, optimise: false).Stdout);
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
                    "import Sable.Math\n@extern(\"c\") fn cbrt(_ x: F64) -> F64",
                    """
                        print(cbrt(8.0))
                        print(F64.cbrt(8.0))
                    """
                )
            ).Stdout
        );
    }
}
