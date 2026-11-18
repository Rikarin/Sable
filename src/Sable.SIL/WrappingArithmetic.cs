namespace Sable.SIL;

/// <summary>
///     A build with <c>overflow-checks = false</c>: integer arithmetic that
///     would trap on overflow keeps the low bits of its result instead, as
///     <c>unchecked as</c> does for a conversion.
/// </summary>
/// <remarks>
///     <para>
///         Each <c>add</c>, <c>sub</c>, <c>mul</c>, <c>neg</c> and <c>abs</c>
///         of an integer becomes its <c>wrapping_</c> form, and each
///         conversion to an integer (<c>as</c>, which SYNTAX.md says traps on
///         overflow only in a checked build) becomes <c>unchecked as</c>'s
///         <c>wrap_</c>. The SIL then says what the program does, so the
///         interpreter, the emitter and constant folding each read it as
///         written rather than consulting a setting.
///     </para>
///     <para>
///         What is not overflow still traps: division by zero, a shift by
///         more than the width, and <c>Int.min / -1</c>, which has no
///         result that wrapping would make sensible (Rust traps there too).
///         A <c>const</c> is computed as the compiler reads it, and one that
///         overflows is an error whatever the profile says.
///     </para>
/// </remarks>
public static class WrappingArithmetic {
    public static void Apply(SilModule module) {
        foreach (var function in module.Functions) {
            foreach (var builtin in function.Blocks.SelectMany(static b => b.Instructions).OfType<BuiltinInst>()) {
                if (Wrapping(builtin.Name) is { } wrapping) {
                    builtin.Rename(wrapping);
                }
            }
        }
    }

    /// <summary>The never-trapping form of a builtin that traps on overflow; null for any other.</summary>
    public static string? Wrapping(string name) {
        if (name.StartsWith("convert_", StringComparison.Ordinal)) {
            var parts = name.Split('_');
            return parts.Length == 3 && Type(parts[2]) is { } to && SilType.IsInteger(to) && Type(parts[1]) is not null
                ? "wrap_" + name["convert_".Length..]
                : null;
        }

        var separator = name.LastIndexOf('_');
        if (separator < 0 || Type(name[(separator + 1)..]) is not { } type || !SilType.IsInteger(type)) {
            return null;
        }

        return name[..separator] is "add" or "sub" or "mul" or "neg" or "abs" ? SilBuiltins.WrappingPrefix + name : null;
    }

    static SilType? Type(string name) => SilType.Numbers.FirstOrDefault(n => n.ToString() == name);
}
