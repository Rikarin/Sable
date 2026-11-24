namespace Sable.SIL.Tests;

/// <summary>
///     <c>SIMD&lt;T, N&gt;</c> and <c>SIMDMask&lt;N&gt;</c> lowered: a vector,
///     <c>@simd(T, N)</c>, whose operations are the SIMD builtins in place,
///     each checked against its declaration where it is used.
/// </summary>
public sealed class SimdLoweringTests {
    static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) Lower(params string[] sources) {
        var compilation = Compilation.Create(sources);
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
        return SilGen.Lower(compilation);
    }

    static List<BuiltinInst> Builtins(SilFunction function) => [..function.Blocks.SelectMany(static b => b.Instructions).OfType<BuiltinInst>()];

    [Fact]
    public void AVectorIsItsLanesAndItsArithmeticTheBuiltins() {
        var (module, diagnostics) = Lower(
            """
            module M
            fn axpy(_ a: SIMD<F32, 4>, _ x: SIMD<F32, 4>, _ y: SIMD<F32, 4>) -> SIMD<F32, 4> {
                return a * x + y
            }
            fn mask(_ a: SIMD<I32, 8>) -> SIMDMask<8> {
                return a.lanesLess(than: SIMD<I32, 8>())
            }
            """
        );
        Assert.Empty(diagnostics);

        var axpy = module.Find("M.axpy")!;
        Assert.Equal("(@simd(F32, 4), @simd(F32, 4), @simd(F32, 4)) -> @simd(F32, 4)", axpy.Type.ToString());
        Assert.Equal(["simd_mul", "simd_add"], Builtins(axpy).Select(static b => b.Name));
        Assert.Empty(axpy.Blocks.SelectMany(static b => b.Instructions).OfType<ApplyInst>());

        var mask = module.Find("M.mask")!;
        Assert.Equal(new VectorType(SilType.Bool, 8), mask.Type.Result);
        Assert.Contains(Builtins(mask), static b => b is { Name: "simd_cmp_lt", Type: VectorType { Element: var e, Count: 8 } } && e == SilType.Bool);
    }

    [Fact]
    public void TheInterpreterComputesEachLaneAsTheScalarBuiltin() {
        var (module, diagnostics) = Lower(
            """
            module M
            fn dot(_ x: F64[:], _ y: F64[:]) -> F64 {
                var sum = SIMD<F64, 2>()
                var i = 0
                while i + 2 <= x.count {
                    sum = SIMD<F64, 2>.load(from: x, at: i).fusedMultiplyAdd(SIMD<F64, 2>.load(from: y, at: i), sum)
                    i += 2
                }
                return sum.reduceAdd()
            }
            fn probe() -> F64 {
                return dot([1, 2, 3, 4], [5, 6, 7, 8])
            }
            """
        );
        Assert.Empty(diagnostics);
        Assert.Empty(SilVerifier.Verify(module));
        Assert.Equal(70.0, new SilInterpreter(module).Run("M.probe"));
    }

    [Fact]
    public void ABuiltinThatGivesOtherThanItsDeclarationSaysIsReportedWhereItIsUsed() {
        var compilation = Compilation.Create(
            """
            module Sable.Core
            public struct SIMD<Scalar, N: Int> {
                @builtin("simd_reduce_add")
                public fn misdeclared() -> Bool
            }
            """,
            """
            module M
            fn probe(_ v: SIMD<I32, 4>) -> Bool {
                return v.misdeclared()
            }
            """
        );
        var (_, diagnostics) = SilGen.Lower(compilation);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ErrorCode.ERR_BuiltinSignatureMismatch, diagnostic.Code);
        Assert.Contains("'simd_reduce_add' gives I32 here, where its declaration says Bool", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltinThatDoesNotApplyToTheLanesIsReported() {
        var compilation = Compilation.Create(
            """
            module Sable.Core
            public struct SIMD<Scalar, N: Int> {
                @builtin("simd_sqrt")
                public fn root() -> SIMD<Scalar, N>
            }
            """,
            """
            module M
            fn probe(_ v: SIMD<I32, 4>) -> SIMD<I32, 4> {
                return v.root()
            }
            """
        );
        var (_, diagnostics) = SilGen.Lower(compilation);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ErrorCode.ERR_BuiltinSignatureMismatch, diagnostic.Code);
        Assert.Contains("'simd_sqrt' does not apply", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("simd_add", "@simd(I8, 16)", "@simd(I8, 16)", "@simd(I8, 16)")]
    [InlineData("simd_cmp_eq", "@simd(F64, 2)", "@simd(F64, 2)", "@simd(Bool, 2)")]
    [InlineData("simd_and", "@simd(Bool, 4)", "@simd(Bool, 4)", "@simd(Bool, 4)")]
    [InlineData("simd_concat", "@simd(U16, 4)", "@simd(U16, 4)", "@simd(U16, 8)")]
    [InlineData("simd_zip_low", "@simd(F32, 4)", "@simd(F32, 4)", "@simd(F32, 4)")]
    public void ALaneWiseBuiltinFollowsItsOperands(string name, string left, string right, string result) {
        var signature = SilBuiltins.Resolve(name, [Parse(left), Parse(right)]);
        Assert.NotNull(signature);
        Assert.Equal(result, signature.Result.ToString());
    }

    [Theory]
    [InlineData("simd_sqrt", "@simd(I32, 4)")]
    [InlineData("simd_and", "@simd(F32, 4)")]
    [InlineData("simd_reduce_and", "@simd(F64, 2)")]
    [InlineData("simd_unzip_even", "@simd(I32, 3)")]
    public void ABuiltinThatHasNoMeaningForTheLanesDoesNotResolve(string name, string operand) {
        var vector = Parse(operand);
        Assert.Null(SilBuiltins.Resolve(name, name == "simd_unzip_even" ? [vector, vector] : [vector]));
    }

    static SilType Parse(string type) =>
        SilParser.Parse($"sil @f : $({type}) -> () {{\nbb0(%0 : ${type}):\n  return\n}}\n").Functions[0].Type.Parameters[0];
}
