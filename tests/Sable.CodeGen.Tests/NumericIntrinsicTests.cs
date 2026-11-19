using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The numeric protocols' builtins (lib/Sable.Core, FixedWidthInteger and
///     FloatingPoint): bit counts, rotations, swaps, arithmetic that reports
///     or saturates on overflow, and the floats' IEEE operations — at every
///     width, native and interpreted, held to the same answers; and natively
///     the LLVM intrinsic each is.
/// </summary>
public sealed class NumericIntrinsicTests {
    [Fact]
    public void BitCountsSeeEveryWidth() =>
        Assert.Equal(
            "7 0 8 1\n15 16 3\n31 4 32\n63 0 64\n127 128 1\n0 64 63\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let a: U8 = 1
                        print("\(a.leadingZeroCount) \(a.trailingZeroCount) \((0 as I8).trailingZeroCount) \(I8.min.popCount)")
                        let b: I16 = 1
                        print("\(b.leadingZeroCount) \((0 as U16).leadingZeroCount) \((7 as I16).popCount)")
                        let c: U32 = 1
                        print("\(c.leadingZeroCount) \((16 as U32).trailingZeroCount) \(U32.max.popCount)")
                        print("\(Int.one.leadingZeroCount) \(Int.min.leadingZeroCount) \(UInt.max.popCount)")
                        print("\(U128.one.leadingZeroCount) \(I128.zero.trailingZeroCount) \(I128.one.popCount)")
                        print("\(I64.min.leadingZeroCount) \(I64.zero.popCount + 64) \(I64.min.trailingZeroCount)")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void RotationsAndSwapsWorkAtEveryWidth() =>
        Assert.Equal(
            "3 192 3\n13330 -128\n878082066 2018915346\n72057594037927936 -9223372036854775808\n2 85070591730234615865843651857942052864\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let a: U8 = 0b1000_0001
                        print("\(a.rotatedLeft(by: 1)) \(a.rotatedRight(by: 1)) \(a.rotatedLeft(by: -7))")
                        print("\((0x1234 as U16).byteSwapped) \((1 as I8).bitReversed)")
                        print("\((0x12345678 as U32).rotatedLeft(by: 8)) \((0x12345678 as I32).byteSwapped)")
                        print("\(UInt.one.byteSwapped) \(Int.one.bitReversed)")
                        print("\(U128.one.rotatedLeft(by: 129)) \(I128.one.rotatedRight(by: 2))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void OverflowIsReportedAtEveryWidth() =>
        Assert.Equal(
            "4 true\n-128 true 127\n0 false\n-2 true\n0 true 0\n255 0 -32768\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let (a, aOver) = (250 as U8).overflowingAdd(10)
                        print("\(a) \(aOver)")
                        let (b, bOver) = I8.max.overflowingAdd(1)
                        print("\(b) \(bOver) \(I8.max.saturatingAdd(1))")
                        let (c, cOver) = (5 as U32).overflowingSub(5)
                        print("\(c) \(cOver)")
                        let (d, dOver) = Int.max.overflowingMul(2)
                        print("\(d) \(dOver)")
                        let (e, eOver) = U128.max.overflowingAdd(1)
                        print("\(e) \(eOver) \(U64.zero.saturatingSub(1))")
                        print("\((200 as U8).saturatingAdd(100)) \((10 as U16).saturatingSub(20)) \(I16.min.saturatingSub(1))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TheHighHalfOfAProductIsKeptAtEveryWidth() =>
        Assert.Equal(
            "2 -40 0\n4294967295 4611686018427387904 1\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print("\((200 as U8).multipliedHigh(by: 3)) \((-100 as I8).multipliedHigh(by: 100)) \((1 as I32).multipliedHigh(by: 1))")
                        print("\(U32.max.multipliedHigh(by: U32.max) + 1) \(Int.min.multipliedHigh(by: Int.min)) \(U128.max.multipliedHigh(by: 2))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TheFloatsOperationsAreCorrectlyRounded() =>
        Assert.Equal(
            "1.4142135623730951 1.4142135 1.414\n7.0 7.0 7.0\n-0.5 1.0 -1.0\n3.0 -4.0 2.0 2.0\n-3.0 3.0 -2.0\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print("\((2.0).squareRoot()) \((2.0 as F32).squareRoot()) \((2.0 as F16).squareRoot())")
                        print("\((2.0).fusedMultiplyAdd(3.0, 1.0)) \((2.0 as F32).fusedMultiplyAdd(3.0, 1.0)) \((2.0 as F16).fusedMultiplyAdd(3.0, 1.0))")
                        print("\((7.5).remainder(dividingBy: 2.0)) \((5.0 as F16).remainder(dividingBy: 2.0)) \((7.0 as F32).remainder(dividingBy: 2.0))")
                        print("\((3.7).truncated()) \((-3.2 as F32).floor()) \((2.5 as F16).roundedToNearestOrEven()) \((1.5).roundedToNearestOrEven())")
                        print("\((2.5 as F16).rounded().copySign(-1.0)) \((2.1 as F32).ceiling()) \((-2.5).rounded().maximum(-2.0))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void TheFloatsMinimumAndMaximumPropagateNaN() =>
        Assert.Equal(
            "nan nan 2.0 -0.0\nnan 1.0 0.0\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print("\((2.0).minimum(F64.nan)) \(F32.nan.maximum(1.0)) \((2.0).minimumNumber(F64.nan)) \((0.0).minimum(-0.0))")
                        print("\((1.0 as F16).minimum(F16.nan)) \((1.0 as F16).maximumNumber(F16.nan)) \((-0.0 as F32).maximum(0.0))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ScalingAndTheExponentSplitAFloatAtEveryWidth() =>
        Assert.Equal(
            "1024.0 inf 0.0 5e-324\n3 -2 -1074 -149 -24\n-9223372036854775808 9223372036854775807 9223372036854775807\n32770.0 0.75\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print("\((1.0).scaled(byPowerOfTwo: 10)) \((1.0).scaled(byPowerOfTwo: 5000)) \((1.0).scaled(byPowerOfTwo: -5000)) \((1.0).scaled(byPowerOfTwo: -1074))")
                        print("\((12.0).exponent) \((-0.375 as F32).exponent) \(F64.leastNonzeroMagnitude.exponent) \(F32.leastNonzeroMagnitude.exponent) \(F16.leastNonzeroMagnitude.exponent)")
                        print("\((0.0).exponent) \(F64.infinity.exponent) \(F32.nan.exponent)")
                        print("\((1.0 as F16).scaled(byPowerOfTwo: 15)) \((3.0 as F32).scaled(byPowerOfTwo: -2))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void NextUpStepsOneValueAtEveryWidth() =>
        Assert.Equal(
            "1.0000000000000002 5e-324 5e-324 inf nan\n1.0000001 -3.4028235e+38 1.001 6e-08\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print("\((1.0).nextUp) \((0.0).nextUp) \((-0.0).nextUp) \(F64.infinity.nextUp) \(F64.nan.nextUp)")
                        print("\((1.0 as F32).nextUp) \((-F32.infinity).nextUp) \((1.0 as F16).nextUp) \((0.0 as F16).nextUp)")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AFloatsBitsRoundTrip() =>
        Assert.Equal(
            "4607182418800017408 1065353216 15360\n1.0 1.0 1.0\n9223372036854775808\n",
            AssertAgree(
                Program(
                    "",
                    """
                        print("\((1.0).bits) \((1.0 as F32).bits) \((1.0 as F16).bits)")
                        print("\(F64.fromBits(4607182418800017408)) \(F32.fromBits(1065353216)) \(F16.fromBits(15360))")
                        print((-0.0).bits)
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A 128-bit integer converts to an F16 without compiler-rt's
    ///     '__floattihf', which not every C library links: rounded once,
    ///     and an infinity of its sign past the F16's range.
    /// </summary>
    [Fact]
    public void A128BitIntegerBecomesAHalfRoundedOnce() =>
        Assert.Equal(
            "65500.0 inf -inf 2052.0\n131073.0 inf 0.0 -65500.0\n",
            AssertAgree(
                Program(
                    "",
                    """
                        let a: I128 = 65504
                        let b: I128 = 131073
                        let c: I128 = -170141183460469231731687303715884105728
                        let d: U128 = 2051
                        print("\(F16(a)) \(F16(b)) \(F16(c)) \(F16(d))")
                        print("\(F32(b)) \(F16(U128.max)) \(F16(I128.zero)) \(F16(-a))")
                    """
                )
            ).Stdout
        );

    /// <summary>Natively, each builtin is the LLVM intrinsic it names, at the type's width.</summary>
    [Theory]
    [InlineData("let x: I8 = 3\nprint(x.leadingZeroCount)", "call i8 @llvm.ctlz.i8(")]
    [InlineData("let x: U16 = 3\nprint(x.trailingZeroCount)", "call i16 @llvm.cttz.i16(")]
    [InlineData("let x: U128 = 3\nprint(x.popCount)", "call i128 @llvm.ctpop.i128(")]
    [InlineData("let x: U32 = 3\nprint(x.byteSwapped)", "call i32 @llvm.bswap.i32(")]
    [InlineData("let x: I64 = 3\nprint(x.bitReversed)", "call i64 @llvm.bitreverse.i64(")]
    [InlineData("let x: U8 = 3\nprint(x.rotatedLeft(by: 2))", "call i8 @llvm.fshl.i8(")]
    [InlineData("let x: Int = 3\nprint(x.rotatedRight(by: 2))", "call i64 @llvm.fshr.i64(")]
    [InlineData("let x: I32 = 3\nprint(x.saturatingAdd(4))", "call i32 @llvm.sadd.sat.i32(")]
    [InlineData("let x: U64 = 3\nprint(x.saturatingSub(4))", "call i64 @llvm.usub.sat.i64(")]
    [InlineData("let x: U16 = 3\nlet (v, o) = x.overflowingMul(4)\nprint(o)", "@llvm.umul.with.overflow.i16(")]
    [InlineData("let x: F64 = 3.0\nprint(x.squareRoot())", "call double @llvm.sqrt.f64(")]
    [InlineData("let x: F32 = 3.0\nprint(x.fusedMultiplyAdd(2.0, 1.0))", "call float @llvm.fma.f32(")]
    [InlineData("let x: F16 = 3.0\nprint(x.roundedToNearestOrEven())", "call half @llvm.roundeven.f16(")]
    [InlineData("let x: F64 = 3.0\nprint(x.minimum(2.0))", "call double @llvm.minimum.f64(")]
    [InlineData("let x: F64 = 3.0\nprint(x.scaled(byPowerOfTwo: 2))", "call double @llvm.ldexp.f64.i32(")]
    public void EachIsItsIntrinsic(string body, string call) {
        var result = Pipeline.Compile(new SourceText(Program("", body), "test.sb"), optimised: false);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains(call, result.LlvmIr!, StringComparison.Ordinal);
    }
}
