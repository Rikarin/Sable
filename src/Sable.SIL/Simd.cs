using System.Collections.Immutable;

namespace Sable.SIL;

/// <summary>
///     <c>@simd(T, N)</c>: <c>N</c> lanes of a number or a Bool, side by side —
///     what <c>Sable.Core</c>'s <c>SIMD&lt;T, N&gt;</c> (and, of Bools,
///     <c>SIMDMask&lt;N&gt;</c>) lowers to. A value with no ownership to
///     track, natively LLVM's vector type <c>&lt;N x T&gt;</c>, held in
///     registers; in the interpreter, its lanes.
/// </summary>
public sealed class VectorType(SilType element, int count) : SilType {
    public SilType Element { get; } = element;
    public int Count { get; } = count;
    public override string ToString() => $"@simd({Element}, {Count})";

    /// <summary>What a lane may be: an integer of up to 64 bits, a float, or a Bool (a mask's).</summary>
    public static bool IsLane(SilType type) =>
        type == Bool || IsFloat(type) || IntegerInfo(type) is { Bits: <= 64 };
}

/// <summary>
///     The SIMD builtins (<c>simd_…</c>), each on <see cref="VectorType" />s:
///     declared in <c>lib/Sable.Core/SIMD.sb</c>, natively a vector
///     instruction or a few (LlvmEmitter.Simd.cs), in the interpreter
///     computed lane by lane with the scalar builtin of the same name
///     (SilInterpreter.Simd.cs), so that the two agree.
/// </summary>
/// <remarks>
///     <para>
///         A lane-wise builtin is a scalar one by its name: <c>simd_add</c> on
///         two <c>@simd(I32, 4)</c> is <c>add_I32</c> in each lane, and does
///         what it does — an integer's sum traps on overflow (if any lane
///         overflows), <c>simd_wrapping_add</c> keeps the low bits, a
///         comparison gives a lane of Bool. The rest move lanes about, load
///         and store them, reduce them to one, or convert them.
///     </para>
///     <para>
///         Their signatures follow the vectors they are given: what a
///         declaration of one says, its parameters and result, the
///         builtin must agree with where it is used, once the types are
///         known (SilGen.Builtins.cs).
///     </para>
/// </remarks>
public static class SimdBuiltins {
    public const string Prefix = "simd_";

    public static bool Is(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The scalar builtins that work lane by lane: <c>simd_</c> and one of these is that builtin in each lane.</summary>
    public static readonly ImmutableHashSet<string> LaneWise = [
        "add", "sub", "mul", "div", "rem", "neg", "abs", "min", "max",
        "and", "or", "xor", "not", "shl", "shr",
        "wrapping_add", "wrapping_sub", "wrapping_mul", "wrapping_neg",
        "add_saturating", "sub_saturating",
        "cmp_eq", "cmp_ne", "cmp_lt", "cmp_le", "cmp_gt", "cmp_ge",
        "sqrt", "floor", "ceil", "trunc", "round", "round_even", "fma", "copysign", "minimum", "maximum",
    ];

    /// <summary>The reductions: every lane combined into one value.</summary>
    public static readonly ImmutableHashSet<string> Reductions = ["reduce_add", "reduce_min", "reduce_max", "reduce_and", "reduce_or", "reduce_xor"];

    /// <summary>The operation of a <c>simd_</c> builtin: <c>add</c> of <c>simd_add</c>.</summary>
    public static string Op(string name) => name[Prefix.Length..];

    /// <summary>The scalar builtin a lane-wise one is in each lane of vectors of this element.</summary>
    public static string ScalarOf(string name, SilType element) => $"{Op(name)}_{element}";

    /// <summary>
    ///     Whether a builtin neither traps nor has an effect: what may be
    ///     removed when unused, or computed once for two uses.
    /// </summary>
    public static bool IsPure(string name, IReadOnlyList<SilType> operands) {
        var op = Op(name);
        if (op is "splat" or "select" or "concat" or "low_half" or "high_half" or "zip_low" or "zip_high" or "unzip_even" or "unzip_odd" or "convert"
            || Reductions.Contains(op)) {
            return true;
        }

        if (!LaneWise.Contains(op) || operands is not [VectorType { Element: var element }, ..]) {
            return false;
        }

        // An integer's arithmetic that can trap does; the rest of it, and a float's, never.
        return !SilType.IsInteger(element) || op is not ("add" or "sub" or "mul" or "div" or "rem" or "neg" or "abs" or "shl" or "shr");
    }

    /// <summary>
    ///     The signature of the builtin on operands of these types, or null
    ///     where it does not apply. The splat, a load and a conversion take
    ///     their vector's type from the result they are declared to give.
    /// </summary>
    public static SilBuiltins.Signature? Resolve(string name, IReadOnlyList<SilType> operands, SilType? result) {
        var op = Op(name);
        switch (op) {
            // Every lane the one value.
            case "splat" when operands is [var value] && result is VectorType vector && vector.Element == value:
                return new([value], vector);

            // N elements from the index on; traps unless all are in the slice.
            case "load" when operands is [SliceType slice, var at] && at == SilType.Int && result is VectorType vector && vector.Element == slice.Element
                             && slice.Element != SilType.Bool:
                return new([slice, SilType.Int], vector);

            // The lanes written over N elements from the index on, the slice
            // made the only reference to its elements first; traps unless all are in it.
            case "store" when operands is [AddressType { Pointee: SliceType slice } address, VectorType vector, var at]
                                && at == SilType.Int && vector.Element == slice.Element && slice.Element != SilType.Bool:
                return new([address, vector, SilType.Int], SilType.Unit);

            // One lane, read or replaced; an index outside 0..<N traps.
            case "extract" when operands is [VectorType vector, var at] && at == SilType.Int:
                return new([vector, SilType.Int], vector.Element);

            case "insert" when operands is [VectorType vector, var lane, var at] && at == SilType.Int && lane == vector.Element:
                return new([vector, vector.Element, SilType.Int], vector);

            // Each lane from the first vector where the mask's is true, else from the second.
            case "select" when operands is [VectorType { Element: var flag } mask, VectorType chosen, var other]
                               && flag == SilType.Bool && mask.Count == chosen.Count && other == chosen:
                return new([mask, chosen, chosen], chosen);

            // Lanes picked by index, as many as there are indices: each index outside 0..<N traps.
            case "swizzle" when operands is [VectorType vector, VectorType { Element: var index } indices] && index == SilType.Int:
                return new([vector, indices], new VectorType(vector.Element, indices.Count));

            // The first vector's lanes, then the second's.
            case "concat" when operands is [VectorType first, var second] && second == first:
                return new([first, first], new VectorType(first.Element, first.Count * 2));

            case "low_half" or "high_half" when operands is [VectorType { Count: var count } whole] && count % 2 == 0:
                return new([whole], new VectorType(whole.Element, count / 2));

            // Interleaved: zip_low is a0 b0 a1 b1 … of the low halves, zip_high
            // of the high ones; unzip_even is the even lanes of a then of b,
            // unzip_odd the odd ones — each the other's inverse.
            case "zip_low" or "zip_high" or "unzip_even" or "unzip_odd" when operands is [VectorType { Count: var count } first, var second]
                                                                             && second == first && count % 2 == 0:
                return new([first, first], first);

            // Each lane converted as 'unchecked as' converts it: an integer
            // keeps its low bits; a float to an integer goes toward zero and
            // saturates, NaN giving zero; to a float rounds to the nearest.
            case "convert" when operands is [VectorType { Element: var from } source] && result is VectorType { Element: var to } target
                                && target.Count == source.Count && from != SilType.Bool && to != SilType.Bool:
                return new([source], target);

            // An integer's sum wraps; a float's is added in halves, pairwise
            // (lane i with lane i + N/2, and so on down), which natively and
            // in the interpreter alike is the order it is computed in.
            case "reduce_add" or "reduce_min" or "reduce_max" when operands is [VectorType { Element: var element } vector] && element != SilType.Bool:
                return new([vector], element);

            case "reduce_and" or "reduce_or" or "reduce_xor" when operands is [VectorType { Element: var element } vector] && !SilType.IsFloat(element):
                return new([vector], element);
        }

        if (!LaneWise.Contains(op) || operands is not [VectorType { Element: var laneType, Count: var lanes }, ..]
            || !operands.All(o => o is VectorType { Count: var n } && n == lanes)) {
            return null;
        }

        if (SilBuiltins.Find($"{op}_{laneType}") is not { } laneSignature || laneSignature.Operands.Length != operands.Count) {
            return null;
        }

        for (var i = 0; i < operands.Count; i++) {
            if (((VectorType)operands[i]).Element != laneSignature.Operands[i]) {
                return null;
            }
        }

        return new([..operands], new VectorType(laneSignature.Result, lanes));
    }
}
