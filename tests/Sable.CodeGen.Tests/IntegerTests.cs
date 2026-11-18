using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The sized integers and F32 (docs/03 §3.1), which inline layout stores:
///     arithmetic that traps on overflow at each width, comparisons by
///     signedness, conversions that trap when the value does not fit, and
///     printing. Native and interpreted, held to the same answers.
/// </summary>
public sealed class IntegerTests {
    [Fact]
    public void EachWidthComputesAsItsOwn() =>
        Assert.Equal(
            "200 70000 -5 1099511627776\n255\n18446744073709551615\n6148914691236517205\n1.1\n2.2\n201\n5\n-128\n"
            + "total 17850000\ntrue\ntrue\n1.100000023841858\n65535\n5\n-1 255\n4294967295\n7\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let a: U8 = 200
                        let b: U32 = 70000
                        let c: I16 = -5
                        let d: U64 = U64(1) << 40
                        print("\(a) \(b) \(c) \(d)")
                        var x: U8 = 250
                        x = x + 5
                        print(x)
                        let big: U64 = 18446744073709551615
                        print(big)
                        print(big / 3)
                        let f: F32 = 1.1
                        print(f)
                        print(f * 2)
                        print(Int(a) + 1)
                        print(U8(c + 10))
                        print(I8(-128) / I8(1))
                        let total = U64(x) * U64(b)
                        print("total \(total)")
                        print(c < 0)
                        print(a > U8(100))
                        print(F64(f))
                        print(U16(65535))
                        print(-c)
                        let m: I8 = -1
                        print("\(m) \(U8(255) & x)")
                        print(U32(4294967295) | U32(0))
                        print(U8(15) >> U8(1))
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("var x: U8 = 255\nx = x + 1\nprint(x)", "integer overflow")]
    [InlineData("var x: U16 = 0\nx = x - 1\nprint(x)", "integer overflow")]
    [InlineData("var x: I8 = -128\nx = x / -1\nprint(x)", "integer overflow")]
    [InlineData("let x: I32 = 300000\nprint(I16(x))", "I32 does not fit in I16")]
    [InlineData("let x = -1\nprint(U64(x))", "Int does not fit in U64")]
    [InlineData("let x: F32 = 300.5\nprint(U8(x))", "F32 does not fit in U8")]
    [InlineData("let x: U8 = 1\nprint(x << U8(8))", "shift out of range")]
    public void EachWidthTrapsAtItsOwnLimits(string body, string trap) {
        var execution = AssertAgree(Program("", body));
        Assert.StartsWith($"fatal error: {trap}\n  at test.sb:", execution.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("let x: I32 = 300000\nprint(x as I16)", "I32 does not fit in I16")]
    [InlineData("print(300 as U8)", "Int does not fit in U8")]
    public void AsTrapsWhereTheValueDoesNotFit(string body, string trap) {
        var execution = AssertAgree(Program("", body));
        Assert.StartsWith($"fatal error: {trap}\n  at test.sb:", execution.Stderr, StringComparison.Ordinal);
    }

    /// <summary>
    ///     'unchecked as' (spec/SYNTAX.md §16) never traps: an integer keeps
    ///     its low bits, a float goes toward zero and stops at the bounds.
    /// </summary>
    [Fact]
    public void UncheckedAsWrapsAndTruncates() =>
        Assert.Equal(
            "44\n18446744073709551615\n65535\n4464\n-56\n200\n-1\n2147483647\n-2147483648\n2\n0\n0\n4294967295\n255\n3\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print(300 unchecked as U8)
                        let n = -1
                        print(n unchecked as U64)
                        print(n unchecked as U16)
                        let big: I64 = 70000
                        print(big unchecked as I16)
                        let u: U8 = 200
                        print(u unchecked as I8)
                        print(u unchecked as Int)
                        let all: U64 = 18446744073709551615
                        print(all unchecked as Int)
                        let f = 3.0e10
                        print(f unchecked as I32)
                        print((-f) unchecked as I32)
                        print(2.9 unchecked as U8)
                        print((-2.9) unchecked as U8)
                        let nan = 0.0 / 0.0
                        print(nan unchecked as I32)
                        let wide: F32 = 1.0e20
                        print(wide unchecked as U32)
                        print(wide unchecked as U8)
                        print(3.75 unchecked as I8)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void SizedValuesLiveInStructsArraysAndOptionals() =>
        Assert.Equal(
            "6 7 8\n21\n3\nnone\n",
            AssertAgree(
                Program(
                    """
                    struct Pixel {
                        var r: U8
                        var g: U8
                        var b: U8
                    }

                    fn sum(_ values: U16[]) -> U64 {
                        var total: U64 = 0
                        for v in values {
                            total = total + U64(v)
                        }
                        return total
                    }

                    fn half(_ x: I32) -> I32? {
                        if x % 2 == 1 {
                            return none
                        }
                        return x / 2
                    }
                    """,
                    """
                        let p = Pixel { r: 6, g: 7, b: 8 }
                        print("\(p.r) \(p.g) \(p.b)")
                        print(sum([1, 2, 3, 4, 5, 6]))
                        if let h = half(6) {
                            print(h)
                        }
                        if half(7) == none {
                            print("none")
                        }
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     I128 and U128 (spec/SYNTAX.md §4): literals past 64 bits, arithmetic
    ///     that traps at their own limits, conversions, members and printing.
    /// </summary>
    [Fact]
    public void TheHundredAndTwentyEightBitIntegersComputeAndPrint() =>
        Assert.Equal(
            "340282366920938463463374607431768211455\n-170141183460469231731687303715884105728\n"
            + "7000000000000000000005\n-1\n-3 and 7\n18446744073709551615\n3\n-3.0\n7\ntrue\n"
            + "18446744073709551616\n340282366920938463463374607431768211455\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let big: U128 = 340282366920938463463374607431768211455
                        let low: I128 = -170141183460469231731687303715884105728
                        let x: U128 = 7
                        let y: I128 = -3
                        print(big)
                        print(low)
                        print(x * 1000000000000000000000 + 5)
                        print(y / 2)
                        print("\(y) and \(x.toString())")
                        print((big >> 64) as U64)
                        print(y.abs())
                        print(y as F64)
                        print(x.max(other: 2))
                        print(low < I128(y))
                        let pastU64: U128 = 18446744073709551615
                        print(pastU64 + 1)
                        let all: U128 = 0xFFFF_FFFF_FFFF_FFFF_FFFF_FFFF_FFFF_FFFF
                        print(all)
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("let x: U128 = 340282366920938463463374607431768211455\nprint(x + 1)", "integer overflow")]
    [InlineData("let x: I128 = -170141183460469231731687303715884105728\nprint(-x)", "integer overflow")]
    [InlineData("let x: U128 = 18446744073709551616\nprint(x as U64)", "U128 does not fit in U64")]
    [InlineData("let x: I128 = -1\nprint(x as U128)", "I128 does not fit in U128")]
    public void TheHundredAndTwentyEightBitIntegersTrapAtTheirLimits(string body, string trap) {
        var execution = AssertAgree(Program("", body));
        Assert.StartsWith($"fatal error: {trap}\n  at test.sb:", execution.Stderr, StringComparison.Ordinal);
    }

    /// <summary>F16: half precision, computed and printed with the fewest digits that read back.</summary>
    [Fact]
    public void FSixteenComputesAndPrints() =>
        Assert.Equal(
            "1.5\n4.6\n0.1\n65500.0\n1.5\n7.0\n1\n-2.5\ninf\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let h: F16 = 1.5
                        print(h)
                        print(h * 3.0 + 0.1)
                        let tenth: F16 = 0.1
                        print("\(tenth)")
                        let largest: F16 = 65504.0
                        print(largest)
                        print((-h).abs())
                        let seven: U128 = 7
                        print(seven as F16)
                        print(h as I128)
                        print(F16(-2.5).min(other: h))
                        print(largest * 2.0)
                    """
                )
            ).Stdout
        );

    /// <summary>A character converts to and from an integer through its code point, and prints as itself.</summary>
    [Fact]
    public void ACharacterConvertsThroughItsCodePoint() =>
        Assert.Equal(
            "a\n\u00e9\n65\nB\n233\n233\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print('a')
                        print('\u{E9}')
                        print('A' as U32)
                        let u: U32 = 66
                        print(u as Char)
                        print('\u{E9}' as U8)
                        print('\u{E9}' unchecked as U8)
                    """
                )
            ).Stdout
        );

    [Theory]
    [InlineData("let n: U32 = 55296\nprint(n as Char)", "U32 is not a Unicode scalar, so it is no Char")]
    [InlineData("print('\\u{1F600}' as U8)", "Int does not fit in U8")]
    public void ACharacterConversionTrapsWhereThereIsNoValue(string body, string trap) {
        var execution = AssertAgree(Program("", body));
        Assert.StartsWith($"fatal error: {trap}\n  at test.sb:", execution.Stderr, StringComparison.Ordinal);
    }

    const string Wrapping = """
                                var x: U8 = 255
                                x = x + 1
                                print(x)
                                var i = 9223372036854775807
                                i = i + 1
                                print(i)
                                var m: I8 = -128
                                print(-m)
                                var p: I32 = 65536
                                print(p * p)
                                var u: U16 = 0
                                u = u - 3
                                print(u)
                                var q: U128 = 0
                                q = q - 1
                                print(q)
                                let big = 300
                                print(big as U8)
                                print(I16(70000 + big))
                                var n = -9223372036854775807
                                n = n - 2
                                print(n * 3)
                            """;

    /// <summary>
    ///     A profile's 'overflow-checks = false': what would trap on overflow
    ///     keeps the low bits instead, natively at each level and in the
    ///     interpreter alike, and 'as' wraps as 'unchecked as' does.
    /// </summary>
    [Fact]
    public void WithoutOverflowChecksArithmeticWraps() {
        const string expected = "0\n-9223372036854775808\n-128\n0\n65533\n340282366920938463463374607431768211455\n44\n4764\n9223372036854775805\n";
        var source = Program("", Wrapping);
        Assert.Equal(expected, InterpretWithoutOverflowChecks(source).Output);
        if (!CanBuild) {
            return;
        }

        foreach (var optimise in new[] { false, true }) {
            var execution = BuildAndRunWithoutOverflowChecks(source, optimise);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(expected, execution.Stdout);
        }
    }

    /// <summary>What is not overflow traps whatever the profile says.</summary>
    [Theory]
    [InlineData("var z = 0\nprint(5 / z)", "division by zero")]
    [InlineData("var s: U8 = 9\nprint(U8(1) << s)", "shift out of range")]
    public void WithoutOverflowChecksOtherTrapsRemain(string body, string trap) {
        var source = Program("", body);
        Assert.Equal(trap, InterpretWithoutOverflowChecks(source).Trap?.Message);
        if (CanBuild) {
            var execution = BuildAndRunWithoutOverflowChecks(source, optimise: true);
            Assert.StartsWith($"fatal error: {trap}", execution.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithoutOverflowChecksTheSilSaysItWraps() {
        const string body = "var x: U8 = 255\nx = x + 1\nprint(x)";
        var wrapping = Pipeline.Compile([SyntaxTree.Parse(new SourceText(Program("", body), "test.sb"))], checkOverflow: false);
        Assert.True(wrapping.Succeeded, string.Join("\n", wrapping.Errors));
        Assert.Contains("wrapping_add_U8", wrapping.Sil!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("call { i8, i1 } @llvm.uadd.with.overflow.i8(", wrapping.LlvmIr!, StringComparison.Ordinal);

        var trapping = Pipeline.Compile(new SourceText(Program("", body), "test.sb"));
        Assert.Contains("call { i8, i1 } @llvm.uadd.with.overflow.i8(", trapping.LlvmIr!, StringComparison.Ordinal);
    }

    static (string Output, SilTrap? Trap) InterpretWithoutOverflowChecks(string source) {
        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(source, "test.sb"))], checkOverflow: false);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var interpreter = new SilInterpreter(result.Sil!);
        try {
            interpreter.Run(result.Sil!.EntryPoint!);
            return (interpreter.Output.ToString(), null);
        } catch (SilTrap trap) {
            return (interpreter.Output.ToString(), trap);
        }
    }

    static Execution BuildAndRunWithoutOverflowChecks(string source, bool optimise) {
        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText(source, "test.sb"))], optimised: optimise, checkOverflow: false);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var executable = Platform.Executable(Path.Combine(Path.GetTempPath(), $"sable-test-{Guid.NewGuid():N}"));
        try {
            var build = NativeBuild.Build(result.LlvmIr!, executable, optimise);
            Assert.True(build.Succeeded, build.Output);
            var start = new System.Diagnostics.ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new(stdout.Result, stderr, process.ExitCode);
        } finally {
            Platform.DeleteExecutable(executable);
        }
    }
}
