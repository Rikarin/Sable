using System.Numerics;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     The SIMD builtins (<see cref="SimdBuiltins" />) as LLVM's vector
///     instructions and intrinsics on <c>&lt;N x T&gt;</c>, each lane doing
///     what the scalar builtin of its name does — an integer's checked
///     arithmetic traps if any lane overflows — so that the interpreter,
///     which computes them lane by lane, agrees.
/// </summary>
public sealed partial class LlvmEmitter {
    /// <summary>The vector intrinsics the module's functions call, declared once each after them.</summary>
    readonly SortedSet<string> _simdDeclarations = new(StringComparer.Ordinal);

    sealed partial class FunctionEmitter {
        void Simd(BuiltinInst builtin, string[] operands, Func<string> Result) {
            var op = SimdBuiltins.Op(builtin.Name);
            var first = builtin.Operands[0].Type as VectorType;
            switch (op) {
                case "splat": {
                    var vector = (VectorType)builtin.Type;
                    Line($"{Result()} = {SplatOf(vector, operands[0])}");
                    return;
                }

                // The last element's index checked, as reading it would, and
                // a negative offset refused: then one load, at the element's alignment.
                case "load": {
                    var vector = (VectorType)builtin.Type;
                    NegativeOffsetTraps(operands[1]);
                    var lastIndex = Fresh();
                    Line($"{lastIndex} = add i64 {operands[1]}, {vector.Count - 1}");
                    var last = SliceElement(operands[0], lastIndex, (SliceType)builtin.Operands[0].Type);
                    var start = Fresh();
                    Line($"{start} = getelementptr inbounds {Type(vector.Element)}, ptr {last}, i64 {1 - vector.Count}");
                    Line($"{Result()} = load {Type(vector)}, ptr {start}, align {TypeLayout.Align(vector.Element)}");
                    return;
                }

                case "store": {
                    var vector = (VectorType)builtin.Operands[1].Type;
                    NegativeOffsetTraps(operands[2]);
                    var lastIndex = Fresh();
                    Line($"{lastIndex} = add i64 {operands[2]}, {vector.Count - 1}");
                    // An array's element is reached inline when the array is
                    // its buffer's only reference; a slice's through the runtime.
                    var last = ((AddressType)builtin.Operands[0].Type).Pointee is ArrayType array
                        ? MutableElement(operands[0], lastIndex, array)
                        : SliceMutableElement(operands[0], lastIndex, (SliceType)((AddressType)builtin.Operands[0].Type).Pointee);
                    var start = Fresh();
                    Line($"{start} = getelementptr inbounds {Type(vector.Element)}, ptr {last}, i64 {1 - vector.Count}");
                    Line($"store {Type(vector)} {operands[1]}, ptr {start}, align {TypeLayout.Align(vector.Element)}");
                    return;
                }

                case "extract":
                    LaneIndexTraps(operands[1], first!.Count);
                    Line($"{Result()} = extractelement {Type(first)} {operands[0]}, i64 {operands[1]}");
                    return;

                case "insert":
                    LaneIndexTraps(operands[2], first!.Count);
                    Line($"{Result()} = insertelement {Type(first)} {operands[0]}, {Type(first.Element)} {operands[1]}, i64 {operands[2]}");
                    return;

                case "select": {
                    var chosen = (VectorType)builtin.Operands[1].Type;
                    Line($"{Result()} = select {Type(first!)} {operands[0]}, {Type(chosen)} {operands[1]}, {Type(chosen)} {operands[2]}");
                    return;
                }

                // Each index checked, then each lane read and put in place:
                // with indices known as it is compiled, LLVM makes the
                // chain one shuffle.
                case "swizzle": {
                    var indices = (VectorType)builtin.Operands[1].Type;
                    var outside = Fresh();
                    Line($"{outside} = icmp uge {Type(indices)} {operands[1]}, {Constant(indices, first!.Count.ToString())}");
                    TrapIf(Any(outside, indices.Count), "SIMD lane index out of range");
                    var result = (VectorType)builtin.Type;
                    var built = "poison";
                    for (var i = 0; i < result.Count; i++) {
                        var (index, lane) = (Fresh(), Fresh());
                        Line($"{index} = extractelement {Type(indices)} {operands[1]}, i64 {i}");
                        Line($"{lane} = extractelement {Type(first)} {operands[0]}, i64 {index}");
                        var next = i == result.Count - 1 ? Result() : Fresh();
                        Line($"{next} = insertelement {Type(result)} {built}, {Type(result.Element)} {lane}, i64 {i}");
                        built = next;
                    }

                    return;
                }

                case "concat":
                    Line($"{Result()} = {Shuffle(first!, operands[0], operands[1], Enumerable.Range(0, first!.Count * 2))}");
                    return;

                case "low_half" or "high_half": {
                    var half = first!.Count / 2;
                    Line($"{Result()} = {Shuffle(first, operands[0], "poison", Enumerable.Range(op == "low_half" ? 0 : half, half))}");
                    return;
                }

                case "zip_low" or "zip_high": {
                    var (n, from) = (first!.Count, op == "zip_low" ? 0 : first.Count / 2);
                    Line($"{Result()} = {Shuffle(first, operands[0], operands[1], Enumerable.Range(0, n / 2).SelectMany(i => new[] { from + i, n + from + i }))}");
                    return;
                }

                case "unzip_even" or "unzip_odd": {
                    var (n, parity) = (first!.Count, op == "unzip_even" ? 0 : 1);
                    Line($"{Result()} = {Shuffle(first, operands[0], operands[1], Enumerable.Range(0, n).Select(i => 2 * i + parity))}");
                    return;
                }

                case "convert":
                    _names[builtin] = ConvertLanes(first!, (VectorType)builtin.Type, operands[0]);
                    return;
            }

            if (SimdBuiltins.Reductions.Contains(op)) {
                _names[builtin] = Reduce(op, first!, operands[0]);
                return;
            }

            _names[builtin] = LaneWise(op, first!, operands);
        }

        /// <summary>A lane's index outside 0..<count traps.</summary>
        void LaneIndexTraps(string index, int count) {
            var outside = Fresh();
            Line($"{outside} = icmp uge i64 {index}, {count}");
            TrapIf(outside, "SIMD lane index out of range");
        }

        /// <summary>Whether any lane of a mask is true: an i1.</summary>
        string Any(string mask, int count) {
            var any = Fresh();
            Line($"{any} = call i1 {Intrinsic($"llvm.vector.reduce.or.v{count}i1", "i1", $"<{count} x i1>")}(<{count} x i1> {mask})");
            return any;
        }

        /// <summary>An intrinsic's name, declared for the module the first time it is called: the callee, '@' and all.</summary>
        string Intrinsic(string name, string result, params string[] parameters) {
            module._simdDeclarations.Add($"declare {result} @{name}({string.Join(", ", parameters)})");
            return $"@{name}";
        }

        /// <summary>A call of an intrinsic on vectors of one type, each operand of it: '@llvm.smin.v4i32'.</summary>
        string CallLanes(string intrinsic, VectorType vector, params string[] operands) {
            var t = Type(vector);
            var result = Fresh();
            var callee = Intrinsic($"{intrinsic}.{Suffix(vector)}", t, [..operands.Select(_ => t)]);
            Line($"{result} = call {t} {callee}({string.Join(", ", operands.Select(o => $"{t} {o}"))})");
            return result;
        }

        /// <summary>An intrinsic's type suffix for a vector: 'v4f32', 'v8i16', 'v2bf16'.</summary>
        static string Suffix(VectorType vector) => $"v{vector.Count}{ElementSuffix(vector.Element)}";

        static string ElementSuffix(SilType element) =>
            element == SilType.BF16 ? "bf16"
            : SilType.FloatBits(element) is { } bits ? $"f{bits}"
            : element == SilType.Bool ? "i1"
            : $"i{SilType.IntegerInfo(element)!.Value.Bits}";

        /// <summary>A constant vector of one value in every lane.</summary>
        static string Constant(VectorType vector, string value) =>
            $"<{string.Join(", ", Enumerable.Repeat($"{Type(vector.Element)} {value}", vector.Count))}>";

        /// <summary>The instruction that makes a vector of one value in every lane.</summary>
        string SplatOf(VectorType vector, string value) {
            var single = Fresh();
            Line($"{single} = insertelement {Type(vector)} poison, {Type(vector.Element)} {value}, i64 0");
            return $"shufflevector {Type(vector)} {single}, {Type(vector)} poison, <{vector.Count} x i32> zeroinitializer";
        }

        /// <summary>A shuffle of two vectors' lanes (the second's numbered after the first's) into as many as the mask has.</summary>
        static string Shuffle(VectorType vector, string a, string b, IEnumerable<int> mask) {
            var lanes = mask.ToList();
            return $"shufflevector {Type(vector)} {a}, {Type(vector)} {b}, <{lanes.Count} x i32> <{string.Join(", ", lanes.Select(static i => $"i32 {i}"))}>";
        }

        /// <summary>
        ///     A lane-wise builtin: the scalar's instruction, or its intrinsic,
        ///     on the whole vector. A BF16's are computed in wider floats and
        ///     rounded back, as the scalar's are: an F32 for the arithmetic, a
        ///     double for the IEEE operations, so that each rounds as the
        ///     interpreter's lane does.
        /// </summary>
        string LaneWise(string op, VectorType vector, string[] operands) {
            var element = vector.Element;
            var t = Type(vector);
            if (element == SilType.BF16) {
                var arithmetic = op is "add" or "sub" or "mul" or "div" or "rem" or "neg" or "abs" or "min" or "max" || op.StartsWith("cmp_", StringComparison.Ordinal);
                var wide = new VectorType(arithmetic ? SilType.F32 : SilType.F64, vector.Count);
                var widened = operands.Select(o => {
                    var w = Fresh();
                    Line($"{w} = fpext {t} {o} to {Type(wide)}");
                    return w;
                }).ToArray();

                var computed = LaneWise(op, wide, widened);
                if (op.StartsWith("cmp_", StringComparison.Ordinal)) {
                    return computed;
                }

                var narrowed = Fresh();
                Line($"{narrowed} = fptrunc {Type(wide)} {computed} to {t}");
                return narrowed;
            }

            var result = Fresh();
            if (SilType.IsFloat(element)) {
                switch (op) {
                    case "add" or "sub" or "mul" or "div" or "rem":
                        Line($"{result} = f{op} {t} {operands[0]}, {operands[1]}");
                        return result;
                    case "neg":
                        Line($"{result} = fneg {t} {operands[0]}");
                        return result;
                    case "abs":
                        return CallLanes("llvm.fabs", vector, operands[0]);
                    case "min" or "max":
                        return CallLanes(op == "min" ? "llvm.minnum" : "llvm.maxnum", vector, operands);
                    case "sqrt" or "floor" or "ceil" or "trunc" or "round" or "copysign" or "minimum" or "maximum" or "fma":
                        return CallLanes($"llvm.{op}", vector, operands);
                    // To nearest, ties to even, as the scalar's: 'nearbyint' (LlvmEmitter's NumericIntrinsic says why).
                    case "round_even":
                        return CallLanes("llvm.nearbyint", vector, operands[0]);
                }

                var predicate = op[4..] switch { "eq" => "oeq", "ne" => "une", "lt" => "olt", "le" => "ole", "gt" => "ogt", _ => "oge" };
                Line($"{result} = fcmp {predicate} {t} {operands[0]}, {operands[1]}");
                return result;
            }

            if (element == SilType.Bool) {
                switch (op) {
                    case "and" or "or" or "xor":
                        Line($"{result} = {op} {t} {operands[0]}, {operands[1]}");
                        return result;
                    case "not":
                        Line($"{result} = xor {t} {operands[0]}, {Constant(vector, "true")}");
                        return result;
                    default:
                        Line($"{result} = icmp {op[4..]} {t} {operands[0]}, {operands[1]}");
                        return result;
                }
            }

            var (bits, signed) = SilType.IntegerInfo(element)!.Value;
            var s = signed ? "s" : "u";
            switch (op) {
                case "add" or "sub" or "mul":
                    return CheckedLanes($"{s}{op}", vector, operands[0], operands[1]);

                case "neg":
                    return CheckedLanes($"{s}sub", vector, "zeroinitializer", operands[0]);

                case "wrapping_add" or "wrapping_sub" or "wrapping_mul":
                    Line($"{result} = {op[9..]} {t} {operands[0]}, {operands[1]}");
                    return result;

                case "wrapping_neg":
                    Line($"{result} = sub {t} zeroinitializer, {operands[0]}");
                    return result;

                case "div" or "rem": {
                    var zero = Fresh();
                    Line($"{zero} = icmp eq {t} {operands[1]}, zeroinitializer");
                    TrapIf(Any(zero, vector.Count), "division by zero");
                    if (signed) {
                        var (isMin, isMinusOne, both) = (Fresh(), Fresh(), Fresh());
                        Line($"{isMin} = icmp eq {t} {operands[0]}, {Constant(vector, (-(BigInteger.One << (bits - 1))).ToString())}");
                        Line($"{isMinusOne} = icmp eq {t} {operands[1]}, {Constant(vector, "-1")}");
                        Line($"{both} = and <{vector.Count} x i1> {isMin}, {isMinusOne}");
                        TrapIf(Any(both, vector.Count), "integer overflow");
                    }

                    Line($"{result} = {s}{op} {t} {operands[0]}, {operands[1]}");
                    return result;
                }

                case "shl" or "shr": {
                    var outside = Fresh();
                    Line($"{outside} = icmp uge {t} {operands[1]}, {Constant(vector, bits.ToString())}");
                    TrapIf(Any(outside, vector.Count), "shift out of range");
                    Line($"{result} = {(op == "shl" ? "shl" : signed ? "ashr" : "lshr")} {t} {operands[0]}, {operands[1]}");
                    return result;
                }

                case "and" or "or" or "xor":
                    Line($"{result} = {op} {t} {operands[0]}, {operands[1]}");
                    return result;

                case "not":
                    Line($"{result} = xor {t} {operands[0]}, {Constant(vector, "-1")}");
                    return result;

                case "abs": {
                    var isMin = Fresh();
                    Line($"{isMin} = icmp eq {t} {operands[0]}, {Constant(vector, (-(BigInteger.One << (bits - 1))).ToString())}");
                    TrapIf(Any(isMin, vector.Count), "integer overflow");
                    var (negated, negative) = (Fresh(), Fresh());
                    Line($"{negated} = sub {t} zeroinitializer, {operands[0]}");
                    Line($"{negative} = icmp slt {t} {operands[0]}, zeroinitializer");
                    Line($"{result} = select <{vector.Count} x i1> {negative}, {t} {negated}, {t} {operands[0]}");
                    return result;
                }

                case "min" or "max":
                    return CallLanes($"llvm.{s}{op}", vector, operands);

                case "add_saturating" or "sub_saturating":
                    return CallLanes($"llvm.{s}{op[..3]}.sat", vector, operands);
            }

            var relation = op[4..];
            var integerPredicate = relation is "eq" or "ne" ? relation : s + relation;
            Line($"{result} = icmp {integerPredicate} {t} {operands[0]}, {operands[1]}");
            return result;
        }

        /// <summary>Checked arithmetic on every lane: a trap if any overflows.</summary>
        string CheckedLanes(string intrinsic, VectorType vector, string left, string right) {
            var t = Type(vector);
            var pair = $"{{ {t}, <{vector.Count} x i1> }}";
            var (called, overflowed, result) = (Fresh(), Fresh(), Fresh());
            var callee = Intrinsic($"llvm.{intrinsic}.with.overflow.{Suffix(vector)}", pair, t, t);
            Line($"{called} = call {pair} {callee}({t} {left}, {t} {right})");
            Line($"{overflowed} = extractvalue {pair} {called}, 1");
            TrapIf(Any(overflowed, vector.Count), "integer overflow");
            Line($"{result} = extractvalue {pair} {called}, 0");
            return result;
        }

        /// <summary>
        ///     Every lane combined into one. An integer's are LLVM's reductions,
        ///     which no order changes; a float's are halved and added (or
        ///     compared) pairwise, lane i with lane i + N/2, down to one — the
        ///     order the interpreter computes them in — and three lanes are
        ///     (a + b) + c.
        /// </summary>
        string Reduce(string op, VectorType vector, string value) {
            var element = vector.Element;
            var t = Type(element);
            if (!SilType.IsFloat(element)) {
                var (_, signed) = SilType.IntegerInfo(element) ?? (1, false);
                var kind = op switch {
                    "reduce_add" => "add",
                    "reduce_min" => signed ? "smin" : "umin",
                    "reduce_max" => signed ? "smax" : "umax",
                    _ => op[7..],
                };

                var reduced = Fresh();
                var callee = Intrinsic($"llvm.vector.reduce.{kind}.{Suffix(vector)}", t, Type(vector));
                Line($"{reduced} = call {t} {callee}({Type(vector)} {value})");
                return reduced;
            }

            var lane = op switch { "reduce_add" => "add", "reduce_min" => "min", _ => "max" };
            if ((vector.Count & (vector.Count - 1)) != 0) {
                var one = new VectorType(element, 1);
                var accumulated = Fresh();
                Line($"{accumulated} = {Shuffle(vector, value, "poison", [0])}");
                for (var i = 1; i < vector.Count; i++) {
                    var next = Fresh();
                    Line($"{next} = {Shuffle(vector, value, "poison", [i])}");
                    accumulated = LaneWise(lane, one, [accumulated, next]);
                }

                var single = Fresh();
                Line($"{single} = extractelement {Type(one)} {accumulated}, i64 0");
                return single;
            }

            var (current, count) = (value, vector.Count);
            while (count > 1) {
                var whole = new VectorType(element, count);
                var half = count / 2;
                var (low, high) = (Fresh(), Fresh());
                Line($"{low} = {Shuffle(whole, current, "poison", Enumerable.Range(0, half))}");
                Line($"{high} = {Shuffle(whole, current, "poison", Enumerable.Range(half, half))}");
                current = LaneWise(lane, new VectorType(element, half), [low, high]);
                count = half;
            }

            var result = Fresh();
            Line($"{result} = extractelement <1 x {t}> {current}, i64 0");
            return result;
        }

        /// <summary>
        ///     Each lane converted as 'unchecked as' converts it (the scalar
        ///     'wrap_' builtins): an integer keeps its low bits; a float to an
        ///     integer goes toward zero and saturates, NaN giving zero; to a
        ///     float rounds once. A BF16 goes through a double, as the scalar
        ///     does; an integer to an F16 too.
        /// </summary>
        string ConvertLanes(VectorType from, VectorType to, string value) {
            var (a, b) = (from.Element, to.Element);
            if (a == b || Type(a) == Type(b)) {
                return value;
            }

            var result = Fresh();
            var wide = new VectorType(SilType.F64, from.Count);
            if (a == SilType.BF16) {
                Line($"{result} = fpext {Type(from)} {value} to {Type(wide)}");
                return ConvertLanes(wide, to, result);
            }

            if (b == SilType.BF16 || b == SilType.F16 && SilType.IsInteger(a)) {
                var asDouble = a == SilType.F64 ? value : ConvertLanes(from, wide, value);
                Line($"{result} = fptrunc {Type(wide)} {asDouble} to {Type(to)}");
                return result;
            }

            var fromInteger = SilType.IntegerInfo(a);
            var toInteger = SilType.IntegerInfo(b);
            if (fromInteger is var (fromBits, fromSigned) && toInteger is var (toBits, _)) {
                Line($"{result} = {(toBits < fromBits ? "trunc" : fromSigned ? "sext" : "zext")} {Type(from)} {value} to {Type(to)}");
                return result;
            }

            if (fromInteger is var (_, signedSource)) {
                Line($"{result} = {(signedSource ? "sitofp" : "uitofp")} {Type(from)} {value} to {Type(to)}");
                return result;
            }

            if (toInteger is var (_, signedTarget)) {
                // A half is widened first, exactly, for the saturating conversion.
                var source = from;
                if (a == SilType.F16) {
                    source = new VectorType(SilType.F32, from.Count);
                    var widened = Fresh();
                    Line($"{widened} = fpext {Type(from)} {value} to {Type(source)}");
                    value = widened;
                }

                var callee = Intrinsic($"llvm.{(signedTarget ? "fptosi" : "fptoui")}.sat.{Suffix(to)}.{Suffix(source)}", Type(to), Type(source));
                Line($"{result} = call {Type(to)} {callee}({Type(source)} {value})");
                return result;
            }

            var fromWidth = SilType.FloatBits(a)!.Value;
            var toWidth = SilType.FloatBits(b)!.Value;
            Line($"{result} = {(toWidth > fromWidth ? "fpext" : "fptrunc")} {Type(from)} {value} to {Type(to)}");
            return result;
        }
    }
}
