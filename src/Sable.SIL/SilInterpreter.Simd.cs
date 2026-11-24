namespace Sable.SIL;

/// <summary>
///     The SIMD builtins (<see cref="SimdBuiltins" />), lane by lane: each
///     lane-wise one is its scalar builtin in every lane, so that what the
///     interpreter computes is what the native vector instructions do.
/// </summary>
public sealed partial class SilInterpreter {
    /// <summary>A <see cref="VectorType" />'s value: its lanes, each held as a scalar of the lane's type is. Never changed in place.</summary>
    sealed class SimdValue(object?[] lanes) : IEquatable<SimdValue> {
        public object?[] Lanes { get; } = lanes;

        public bool Equals(SimdValue? other) => other is not null && Lanes.SequenceEqual(other.Lanes);
        public override bool Equals(object? obj) => Equals(obj as SimdValue);

        public override int GetHashCode() {
            var hash = new HashCode();
            foreach (var lane in Lanes) {
                hash.Add(lane);
            }

            return hash.ToHashCode();
        }

        public override string ToString() => $"<{string.Join(", ", Lanes)}>";
    }

    /// <summary>A scalar builtin on scalars, as the interpreter computes it anywhere.</summary>
    static object? ScalarBuiltin(string name, object?[] operands) =>
        NumericIntrinsic(name, operands) ?? Numeric(name, operands) ?? Builtin(name, operands);

    object? Simd(BuiltinInst builtin, object?[] operands) {
        var op = SimdBuiltins.Op(builtin.Name);
        static object?[] Lanes(object? value) => ((SimdValue)value!).Lanes;

        switch (op) {
            case "splat": {
                var lanes = new object?[((VectorType)builtin.Type).Count];
                Array.Fill(lanes, operands[0]);
                return new SimdValue(lanes);
            }

            case "load": {
                var count = ((VectorType)builtin.Type).Count;
                var (buffer, start, length) = ElementsOf(operands[0]);
                var at = (long)operands[1]!;
                if (at < 0 || at > length - count) {
                    throw new SilTrap("array index out of range");
                }

                var lanes = new object?[count];
                for (var i = 0; i < count; i++) {
                    lanes[i] = buffer!.Elements[start + (int)at + i];
                }

                return new SimdValue(lanes);
            }

            case "store": {
                var lanes = Lanes(operands[1]);
                var at = (long)operands[2]!;
                if (at < 0 || at > long.MaxValue - lanes.Length) {
                    throw new SilTrap("array index out of range");
                }

                // The last element's index checked, and the elements made unique, as a write of it would.
                var (array, last) = Writable(builtin.Operands[0], (IReference)operands[0]!, at + lanes.Length - 1);
                for (var i = 0; i < lanes.Length; i++) {
                    array.Elements[last - lanes.Length + 1 + i] = lanes[i];
                }

                return null;
            }

            case "extract": {
                var lanes = Lanes(operands[0]);
                return lanes[(int)LaneIndex((long)operands[1]!, lanes.Length)];
            }

            case "insert": {
                var lanes = (object?[])Lanes(operands[0]).Clone();
                lanes[(int)LaneIndex((long)operands[2]!, lanes.Length)] = operands[1];
                return new SimdValue(lanes);
            }

            case "select": {
                var (mask, chosen, other) = (Lanes(operands[0]), Lanes(operands[1]), Lanes(operands[2]));
                return new SimdValue([..mask.Select((flag, i) => (bool)flag! ? chosen[i] : other[i])]);
            }

            case "swizzle": {
                var (lanes, indices) = (Lanes(operands[0]), Lanes(operands[1]));
                return new SimdValue([..indices.Select(index => lanes[(int)LaneIndex(Convert.ToInt64(index), lanes.Length)])]);
            }

            case "mask_bits": {
                var lanes = Lanes(operands[0]);
                var bits = 0ul;
                for (var i = 0; i < lanes.Length; i++) {
                    bits |= (bool)lanes[i]! ? 1ul << i : 0;
                }

                return bits;
            }

            case "concat":
                return new SimdValue([..Lanes(operands[0]), ..Lanes(operands[1])]);

            case "low_half" or "high_half": {
                var lanes = Lanes(operands[0]);
                var half = lanes.Length / 2;
                return new SimdValue(op == "low_half" ? lanes[..half] : lanes[half..]);
            }

            case "zip_low" or "zip_high": {
                var (a, b) = (Lanes(operands[0]), Lanes(operands[1]));
                var from = op == "zip_low" ? 0 : a.Length / 2;
                var lanes = new object?[a.Length];
                for (var i = 0; i < a.Length / 2; i++) {
                    lanes[2 * i] = a[from + i];
                    lanes[2 * i + 1] = b[from + i];
                }

                return new SimdValue(lanes);
            }

            case "unzip_even" or "unzip_odd": {
                var (a, b) = (Lanes(operands[0]), Lanes(operands[1]));
                var parity = op == "unzip_even" ? 0 : 1;
                var lanes = new object?[a.Length];
                for (var i = 0; i < a.Length / 2; i++) {
                    lanes[i] = a[2 * i + parity];
                    lanes[a.Length / 2 + i] = b[2 * i + parity];
                }

                return new SimdValue(lanes);
            }

            case "convert": {
                var (from, to) = (((VectorType)builtin.Operands[0].Type).Element, ((VectorType)builtin.Type).Element);
                return new SimdValue([..Lanes(operands[0]).Select(lane => WrapNumber(lane, from, to))]);
            }
        }

        var element = ((VectorType)builtin.Operands[0].Type).Element;
        if (SimdBuiltins.Reductions.Contains(op)) {
            return Reduce(op, element, Lanes(operands[0]));
        }

        // Lane-wise: the scalar builtin in each lane. A division checks every
        // divisor for zero first, as the native code does before it divides.
        var scalar = SimdBuiltins.ScalarOf(builtin.Name, element);
        var all = operands.Select(Lanes).ToArray();
        if (op is "div" or "rem" && SilType.IsInteger(element) && all[1].Any(static d => d is long l ? l == 0 : d is ulong u && u == 0)) {
            throw new SilTrap("division by zero");
        }

        var result = new object?[all[0].Length];
        for (var i = 0; i < result.Length; i++) {
            result[i] = ScalarBuiltin(scalar, [..all.Select(lanes => lanes[i])]);
        }

        return new SimdValue(result);
    }

    /// <summary>A lane's index, which traps outside 0..<count.</summary>
    static long LaneIndex(long index, int count) =>
        index >= 0 && index < count ? index : throw new SilTrap("SIMD lane index out of range");

    /// <summary>
    ///     Every lane combined: an integer's in order (its sum wrapping, which
    ///     no order changes); a float's in halves, lane i with lane i + N/2,
    ///     down to one — the order the native code adds them in. Three lanes
    ///     are (a + b) + c.
    /// </summary>
    static object? Reduce(string op, SilType element, object?[] lanes) {
        var scalar = op switch {
            "reduce_add" => SilType.IsFloat(element) ? $"add_{element}" : $"wrapping_add_{element}",
            "reduce_min" => $"min_{element}",
            "reduce_max" => $"max_{element}",
            "reduce_and" => $"and_{element}",
            "reduce_or" => $"or_{element}",
            _ => $"xor_{element}",
        };

        if (!SilType.IsFloat(element) || (lanes.Length & (lanes.Length - 1)) != 0) {
            var accumulated = lanes[0];
            for (var i = 1; i < lanes.Length; i++) {
                accumulated = ScalarBuiltin(scalar, [accumulated, lanes[i]]);
            }

            return accumulated;
        }

        var current = lanes;
        while (current.Length > 1) {
            var half = current.Length / 2;
            var next = new object?[half];
            for (var i = 0; i < half; i++) {
                next[i] = ScalarBuiltin(scalar, [current[i], current[half + i]]);
            }

            current = next;
        }

        return current[0];
    }
}
