using System.Text;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     The elementary functions (<see cref="FloatFunctions" />): LLVM's
///     intrinsic where LLVM 18 has one, so that LLVM can fold a constant,
///     vectorise a loop and use an instruction (<c>sqrt</c>); otherwise
///     libm's function by its C name. Either way it is libm that computes
///     it, as the interpreter's <c>Libm</c> does. With them, the byte swaps
///     that reading and writing binary data needs.
/// </summary>
public sealed partial class LlvmEmitter {
    /// <summary>
    ///     libm's functions the intrinsics do not cover, declared in every
    ///     module. They read and write no memory Sable can see: a domain
    ///     error is a NaN, and nothing reads C's errno after one.
    /// </summary>
    static readonly string MathDeclarations = BuildMathDeclarations();

    static string BuildMathDeclarations() {
        var text = new StringBuilder();
        foreach (var function in FloatFunctions.All) {
            foreach (var type in FloatFunctions.Types) {
                var t = FloatType(type);
                var parameters = string.Join(", ", Enumerable.Repeat(t, function.Arity));
                text.Append(function.Intrinsic is { } intrinsic
                    ? $"declare {t} @llvm.{intrinsic}.{IntrinsicSuffix(type)}({parameters})\n"
                    : $"declare {t} @{FloatFunctions.CName(function, type)}({parameters}) nounwind willreturn memory(none)\n");
            }
        }

        // An integer's bytes reversed, 'byte_swap_', is declared with the
        // numeric protocols' other intrinsics (IntrinsicDeclarations).
        return text.ToString();
    }

    static string FloatType(SilType type) => type == SilType.F32 ? "float" : "double";

    static string IntrinsicSuffix(SilType type) => type == SilType.F32 ? "f32" : "f64";

    /// <summary>The call that computes the function of these operands.</summary>
    static string MathCall(FloatFunctions.Function function, SilType type, IEnumerable<string> operands) {
        var t = FloatType(type);
        var callee = function.Intrinsic is { } intrinsic ? $"llvm.{intrinsic}.{IntrinsicSuffix(type)}" : FloatFunctions.CName(function, type);
        return $"call {t} @{callee}({string.Join(", ", operands.Select(o => $"{t} {o}"))})";
    }
}
