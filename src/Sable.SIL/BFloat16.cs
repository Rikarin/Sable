namespace Sable.SIL;

/// <summary>
///     A bfloat16, as the interpreter holds a BF16: its 16 bits, an F32's top
///     half — the sign, the 8 exponent bits, and 7 of the fraction's. Every
///     operation is the F32 one, rounded back to the nearest bfloat16, ties to
///     even, as the native code computes it: an F32 has room enough that the
///     two roundings never disagree. From an F64 it is rounded once, through
///     an F32 rounded to odd, as LLVM's conversion is.
/// </summary>
public readonly record struct BFloat16(ushort Bits) {
    public float ToSingle() => BitConverter.UInt32BitsToSingle((uint)Bits << 16);

    public double ToDouble() => ToSingle();

    /// <summary>The F32 rounded to the nearest bfloat16, ties to even; a NaN stays one, made quiet.</summary>
    public static BFloat16 FromSingle(float value) {
        var bits = BitConverter.SingleToUInt32Bits(value);
        if (float.IsNaN(value)) {
            return new BFloat16((ushort)((bits >> 16) | 0x0040));
        }

        var rounded = bits + 0x7FFFu + ((bits >> 16) & 1u);
        return new BFloat16((ushort)(rounded >> 16));
    }

    /// <summary>
    ///     The F64 rounded once to the nearest bfloat16: through an F32 rounded
    ///     to odd — its last bit set where anything was cut off — which keeps
    ///     the second rounding from landing on a tie the first made.
    /// </summary>
    public static BFloat16 FromDouble(double value) {
        if (double.IsNaN(value)) {
            return FromSingle(float.NaN);
        }

        var single = (float)value;
        if (float.IsFinite(single) && (double)single != value) {
            var bits = BitConverter.SingleToUInt32Bits(single);
            // Toward zero first, then odd: an F32 nearer zero than the F64.
            if (Math.Abs((double)single) > Math.Abs(value)) {
                bits -= 1;
            }

            single = BitConverter.UInt32BitsToSingle(bits | 1u);
        }

        return FromSingle(single);
    }

    public bool IsNaN => (Bits & 0x7FFF) > 0x7F80;

    public override string ToString() => ToSingle().ToString(System.Globalization.CultureInfo.InvariantCulture);
}
