namespace Sable.Symbols.Tests;

/// <summary>
///     The type rules of Sable.Core's <c>SIMD&lt;Scalar, N&gt;</c> and
///     <c>SIMDMask&lt;N&gt;</c>: which lanes and counts a vector may have, the
///     operators its constrained extensions give and to which lanes, literal
///     operands, and the SIMD builtins' leave to be generic.
/// </summary>
public sealed class SimdTypeTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(params string[] sources) => [
        ..Compilation.Create(sources)
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static Diagnostic SingleError(string source) => Assert.Single(ErrorsIn(source));

    [Fact]
    public void EveryIntegerAndFloatIsALane() =>
        AssertClean(
            """
            module M
            fn probe() {
                let a = SIMD<I8, 16>() + SIMD<I8, 16>(repeating: 1)
                let b = SIMD<F16, 8>(repeating: 1) * SIMD<F16, 8>.zero
                let c: SIMD<Int, 3> = SIMD<Int, 3>([1, 2, 3])
                let d: SIMD<UInt, 64> = SIMD<UInt, 64>()
                let e: SIMD<F64, 32> = SIMD<F64, 32>()
                let f = SIMD<BF16, 2>() - SIMD<BF16, 2>()
                let g = SIMD<U64, 2>.zero.lanesEqual(SIMD<U64, 2>()).trueCount
            }
            """
        );

    [Fact]
    public void ABoolIsNotALane() {
        var error = SingleError("module M\nfn probe(_ v: SIMD<Bool, 4>) { }");
        Assert.Contains("SIMDScalar", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SIMD<F32, 5>")]
    [InlineData("SIMD<F32, 1>")]
    [InlineData("SIMD<I8, 128>")]
    [InlineData("SIMDMask<6>")]
    public void ALaneCountOtherThanTwoThreeOrAPowerOfTwoUpTo64IsRefused(string type) {
        var error = SingleError($"module M\nfn probe(_ v: {type}) {{ }}");
        Assert.Equal("S3421", error.Id);
    }

    [Fact]
    public void AnIntegersOperatorsAreNotAFloats() {
        Assert.Equal("S3009", SingleError("module M\nfn probe(_ v: SIMD<F32, 4>) -> SIMD<F32, 4> { return v & v }").Id);
        Assert.Equal("S3009", SingleError("module M\nfn probe(_ v: SIMD<F32, 4>) -> SIMD<F32, 4> { return ~v }").Id);
        Assert.Equal("S3009", SingleError("module M\nfn probe(_ v: SIMD<U8, 4>) -> SIMD<U8, 4> { return -v }").Id);
        AssertClean("module M\nfn probe(_ v: SIMD<U8, 4>, _ w: SIMD<I8, 4>) -> SIMD<I8, 4> { return SIMD<I8, 4>.converting(~v & v) &+ -w }");
    }

    [Fact]
    public void AFloatsOperationsAreNotAnIntegers() {
        Assert.Equal("S3023", SingleError("module M\nfn probe(_ v: SIMD<I32, 4>) -> SIMD<I32, 4> { return v.squareRoot() }").Id);
        AssertClean("module M\nfn probe(_ v: SIMD<F64, 2>) -> SIMD<F64, 2> { return v.squareRoot().floor() }");
    }

    [Fact]
    public void ALiteralOperandTakesTheLanesType() =>
        AssertClean(
            """
            module M
            fn probe(_ v: SIMD<F32, 4>, _ w: SIMD<U8, 16>) -> SIMD<F32, 4> {
                let x = 2 * v + 1.5
                let y = w * 3 - 1
                return x / 2
            }
            """
        );

    [Fact]
    public void AnArrayLiteralOfIntegersAndFloatsTakesTheElementWanted() =>
        AssertClean(
            """
            module M
            fn probe() -> SIMD<F32, 4> {
                let xs: F32[>..] = [1.5, 3, -2, 0.25]
                return SIMD<F32, 4>([1.5, 3, -2, 0.25]) + SIMD<F32, 4>.load(from: xs, at: 0)
            }
            """
        );

    [Fact]
    public void OnlyASimdBuiltinMayBeGeneric() {
        var errors = ErrorsIn(
            """
            module Sable.Core
            public struct Box<T> {
                @builtin("simd_splat")
                static fn splat<N: Int>(_ value: T) -> Box<T>

                @builtin("string_count")
                fn counted<N: Int>() -> Int
            }
            """
        );
        var error = Assert.Single(errors);
        Assert.Contains("cannot be generic", error.Message, StringComparison.Ordinal);
    }
}
