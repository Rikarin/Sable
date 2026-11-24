using System.Text;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     The elementary functions (<see cref="FloatFunctions" />): libm's
///     function by its C name, called <c>nobuiltin</c>. LLVM knows these
///     names (and has intrinsics for some), and given a constant it folds a
///     call with the compiler's own arithmetic — an F32 one in F64, rounded —
///     or rewrites it as another function; either can differ from the
///     target's libm by an ulp (macOS's <c>atan2f(-0, -1)</c> is the F32
///     below pi, LLVM's fold the one above). Not recognised as a builtin, a
///     call is left as it is, so native code at every level and the
///     interpreter's <c>Libm</c> compute each the same way, and the
///     declaration's <c>memory(none)</c> still lets LLVM share, hoist and
///     drop calls. With them, the byte swaps that reading and writing binary
///     data needs.
/// </summary>
public sealed partial class LlvmEmitter {
    /// <summary>
    ///     libm's functions, declared in every module. They read and write no
    ///     memory Sable can see: a domain error is a NaN, and nothing reads
    ///     C's errno after one.
    /// </summary>
    static readonly string MathDeclarations = BuildMathDeclarations();

    static string BuildMathDeclarations() {
        var text = new StringBuilder();
        foreach (var function in FloatFunctions.All) {
            foreach (var type in FloatFunctions.Types) {
                var t = FloatType(type);
                var parameters = string.Join(", ", Enumerable.Repeat(t, function.Arity));
                text.Append($"declare {t} @{FloatFunctions.CName(function, type)}({parameters}) nounwind willreturn memory(none)\n");
            }
        }

        // The Windows C runtime exports hypotf only as '_hypotf': its
        // 'hypotf' is an inline function in math.h, which IR cannot call.
        text.Append("declare float @_hypotf(float, float) nounwind willreturn memory(none)\n");

        // An integer's bytes reversed, 'byte_swap_', is declared with the
        // numeric protocols' other intrinsics (IntrinsicDeclarations).
        return text.ToString();
    }

    static string FloatType(SilType type) => type == SilType.F32 ? "float" : "double";

    /// <summary>The call that computes the function of these operands: libm's, which LLVM may neither fold nor replace.</summary>
    static string MathCall(FloatFunctions.Function function, SilType type, IEnumerable<string> operands, bool windows) {
        var t = FloatType(type);
        return $"call {t} @{CFunction(function, type, windows)}({string.Join(", ", operands.Select(o => $"{t} {o}"))}) nobuiltin";
    }

    /// <summary>The C function to call for a function of a type on the target: libm's name, except where Windows exports it under another.</summary>
    static string CFunction(FloatFunctions.Function function, SilType type, bool windows) =>
        windows && type == SilType.F32 && function.C == "hypot" ? "_hypotf" : FloatFunctions.CName(function, type);
}
