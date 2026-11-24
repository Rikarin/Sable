using System.Collections.Immutable;

namespace Sable.SIL;

/// <summary>
///     The elementary functions of a float — <c>sin</c>, <c>exp</c>,
///     <c>pow</c>, … — as builtins: <c>math_sin_F64</c>, <c>math_pow_F32</c>.
/// </summary>
/// <remarks>
///     <para>
///         Each is C's function of the same name in the platform's libm
///         (<c>sinf</c> for an F32), computed at the precision of its type.
///         Native code calls it by its C name, at every optimisation level,
///         as a call LLVM may not fold or rewrite (LLVM folds a constant
///         call with its own arithmetic, an F32 one in F64, which can be an
///         ulp from libm's). The interpreter calls the same libm (see
///         <see cref="Libm" />), so a program, its constants and the REPL
///         agree to the bit.
///     </para>
///     <para>
///         None traps and none has an effect: a domain error gives NaN, an
///         overflow an infinity, as C's do. So each is pure for dead-code
///         removal and sharing; a constant the compiler evaluates is the
///         interpreter's call of libm.
///     </para>
/// </remarks>
public static class FloatFunctions {
    public const string Prefix = "math_";

    /// <summary>
    ///     A function: its name in Sable and in C (the F64 one; the F32 one
    ///     adds 'f') and how many operands it takes.
    /// </summary>
    public sealed record Function(string Name, string C, int Arity);

    public static readonly ImmutableArray<Function> All = [
        new("cbrt", "cbrt", 1),
        new("sin", "sin", 1),
        new("cos", "cos", 1),
        new("tan", "tan", 1),
        new("asin", "asin", 1),
        new("acos", "acos", 1),
        new("atan", "atan", 1),
        new("atan2", "atan2", 2),
        new("sinh", "sinh", 1),
        new("cosh", "cosh", 1),
        new("tanh", "tanh", 1),
        new("asinh", "asinh", 1),
        new("acosh", "acosh", 1),
        new("atanh", "atanh", 1),
        new("exp", "exp", 1),
        new("exp2", "exp2", 1),
        new("expm1", "expm1", 1),
        new("log", "log", 1),
        new("log2", "log2", 1),
        new("log10", "log10", 1),
        new("log1p", "log1p", 1),
        new("pow", "pow", 2),
        new("hypot", "hypot", 2),
        new("gamma", "tgamma", 1),
        new("logGamma", "lgamma", 1),
        new("erf", "erf", 1),
        new("erfc", "erfc", 1),
    ];

    /// <summary>The float types the functions are for: those libm has them for.</summary>
    public static readonly ImmutableArray<SilType> Types = [SilType.F64, SilType.F32];

    static readonly Dictionary<string, Function> ByName = All.ToDictionary(static f => f.Name, StringComparer.Ordinal);

    /// <summary>The builtin's name for a function of a type: <c>math_sin_F64</c>.</summary>
    public static string BuiltinName(Function function, SilType type) => $"{Prefix}{function.Name}_{type}";

    /// <summary>The C function a builtin calls, for its type: <c>sin</c>, <c>sinf</c>.</summary>
    public static string CName(Function function, SilType type) => type == SilType.F32 ? function.C + "f" : function.C;

    /// <summary>The function and type a builtin's name names, or null for any other builtin.</summary>
    public static (Function Function, SilType Type)? Find(string name) {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal)) {
            return null;
        }

        var separator = name.LastIndexOf('_');
        if (separator <= Prefix.Length
            || !ByName.TryGetValue(name[Prefix.Length..separator], out var function)
            || Types.FirstOrDefault(t => t.ToString() == name[(separator + 1)..]) is not { } type) {
            return null;
        }

        return (function, type);
    }

    /// <summary>Every function's builtin, for <see cref="SilBuiltins" />' table.</summary>
    internal static IEnumerable<(string Name, SilBuiltins.Signature Signature)> Signatures() =>
        from function in All
        from type in Types
        select (BuiltinName(function, type), new SilBuiltins.Signature([..Enumerable.Repeat(type, function.Arity)], type));
}
