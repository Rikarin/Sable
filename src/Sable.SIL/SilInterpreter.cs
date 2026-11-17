namespace Sable.SIL;

/// <summary>A program stopped where the language says it must: overflow, division by zero, unreachable code.</summary>
public sealed class SilTrap(string message) : Exception(message);

/// <summary>
///     Runs SIL directly, so that tests can check what lowered code
///     <i>computes</i> before there is an LLVM backend to compile it.
/// </summary>
/// <remarks>
///     Values are <see cref="long" /> for <c>Int</c>, <see cref="bool" /> for
///     <c>Bool</c>, <see cref="double" /> for <c>F64</c>, a stack slot for an
///     address, and a function's name for a function value. Integer arithmetic
///     traps on overflow and on division by zero, which is what the generated
///     code will do. A step limit and a depth limit turn a runaway program into
///     a trap rather than a hung test.
/// </remarks>
public sealed class SilInterpreter(SilModule module, int stepLimit = 10_000_000, int depthLimit = 2_000) {
    int _steps;

    /// <summary>Where <c>print</c> writes: one line per call, as the native runtime does.</summary>
    public System.Text.StringBuilder Output { get; } = new();

    sealed class Slot {
        public object? Value;
    }

    public object? Run(string function, params object[] arguments) {
        if (module.Find(function) is not { } target) {
            throw new ArgumentException($"no function @{function}", nameof(function));
        }

        _steps = 0;
        return Call(target, arguments, 0);
    }

    object? Call(SilFunction function, IReadOnlyList<object> arguments, int depth) {
        if (depth > depthLimit) {
            throw new SilTrap("stack overflow: calls nested too deeply");
        }

        var values = new Dictionary<SilValue, object?>();
        var block = function.Entry;
        IReadOnlyList<object?> incoming = arguments;

        while (true) {
            for (var i = 0; i < block.Arguments.Count; i++) {
                values[block.Arguments[i]] = incoming[i];
            }

            SilBlock? next = null;
            foreach (var instruction in block.Instructions) {
                if (++_steps > stepLimit) {
                    throw new SilTrap("step limit reached");
                }

                object? Get(SilValue value) => values[value];

                switch (instruction) {
                    case IntegerLiteralInst literal:
                        values[literal] = literal.Type == SilType.Bool ? literal.Value != 0 : literal.Value;
                        break;

                    case FloatLiteralInst literal:
                        values[literal] = literal.Value;
                        break;

                    case StringLiteralInst literal:
                        values[literal] = literal.Value;
                        break;

                    case BuiltinInst { Name: var name } builtin when name.StartsWith("print_", StringComparison.Ordinal):
                        Output.Append(Format(Get(builtin.Operands[0]))).Append('\n');
                        break;

                    case BuiltinInst builtin:
                        values[builtin] = Builtin(builtin.Name, [..builtin.Operands.Select(Get)]);
                        break;

                    case AllocStackInst alloc:
                        values[alloc] = new Slot();
                        break;

                    case DeallocStackInst:
                        break;

                    case LoadInst load: {
                        var slot = (Slot)Get(load.Address)!;
                        values[load] = slot.Value ?? throw new SilTrap("load from uninitialised storage");
                        break;
                    }

                    case StoreInst store:
                        ((Slot)Get(store.Address)!).Value = Get(store.Value);
                        break;

                    case FunctionRefInst reference:
                        values[reference] = reference.Function;
                        break;

                    case ApplyInst apply: {
                        var name = (string)Get(apply.Callee)!;
                        var callee = module.Find(name) ?? throw new SilTrap($"call to undefined @{name}");
                        values[apply] = Call(callee, [..apply.Arguments.Select(a => Get(a)!)], depth + 1);
                        break;
                    }

                    case BranchInst branch:
                        incoming = [..branch.Operands.Select(Get)];
                        next = branch.Target;
                        break;

                    case CondBranchInst conditional:
                        if ((bool)Get(conditional.Condition)!) {
                            incoming = [..conditional.TrueArguments.Select(Get)];
                            next = conditional.WhenTrue;
                        } else {
                            incoming = [..conditional.FalseArguments.Select(Get)];
                            next = conditional.WhenFalse;
                        }

                        break;

                    case ReturnInst returnInst:
                        return returnInst.Value is { } value ? Get(value) : null;

                    case UnreachableInst:
                        throw new SilTrap("reached 'unreachable'");
                }
            }

            block = next ?? throw new SilTrap("fell off the end of a block");
        }
    }

    static object Builtin(string name, object?[] operands) {
        checked {
            return (name, operands) switch {
                ("add_Int", [long a, long b]) => Wrap(() => a + b),
                ("sub_Int", [long a, long b]) => Wrap(() => a - b),
                ("mul_Int", [long a, long b]) => Wrap(() => a * b),
                ("div_Int", [long a, long b]) => b == 0 ? throw new SilTrap("division by zero") : Wrap(() => a / b),
                ("rem_Int", [long a, long b]) => b == 0 ? throw new SilTrap("division by zero") : Wrap(() => a % b),
                ("and_Int", [long a, long b]) => a & b,
                ("or_Int", [long a, long b]) => a | b,
                ("xor_Int", [long a, long b]) => a ^ b,
                ("shl_Int", [long a, long b]) => b is < 0 or > 63 ? throw new SilTrap("shift out of range") : a << (int)b,
                ("shr_Int", [long a, long b]) => b is < 0 or > 63 ? throw new SilTrap("shift out of range") : a >> (int)b,
                ("neg_Int", [long a]) => Wrap(() => -a),
                ("not_Int", [long a]) => ~a,

                ("add_F64", [double a, double b]) => a + b,
                ("sub_F64", [double a, double b]) => a - b,
                ("mul_F64", [double a, double b]) => a * b,
                ("div_F64", [double a, double b]) => a / b,
                ("neg_F64", [double a]) => -a,

                ("and_Bool", [bool a, bool b]) => a & b,
                ("or_Bool", [bool a, bool b]) => a | b,
                ("xor_Bool", [bool a, bool b]) => a ^ b,
                ("not_Bool", [bool a]) => !a,

                ("cmp_eq_Int", [long a, long b]) => a == b,
                ("cmp_ne_Int", [long a, long b]) => a != b,
                ("cmp_lt_Int", [long a, long b]) => a < b,
                ("cmp_le_Int", [long a, long b]) => a <= b,
                ("cmp_gt_Int", [long a, long b]) => a > b,
                ("cmp_ge_Int", [long a, long b]) => a >= b,

                ("cmp_eq_F64", [double a, double b]) => a == b,
                ("cmp_ne_F64", [double a, double b]) => a != b,
                ("cmp_lt_F64", [double a, double b]) => a < b,
                ("cmp_le_F64", [double a, double b]) => a <= b,
                ("cmp_gt_F64", [double a, double b]) => a > b,
                ("cmp_ge_F64", [double a, double b]) => a >= b,

                ("cmp_eq_Bool", [bool a, bool b]) => a == b,
                ("cmp_ne_Bool", [bool a, bool b]) => a != b,

                _ => throw new SilTrap($"builtin \"{name}\" cannot take these operands"),
            };
        }
    }

    /// <summary>
    ///     A printed value, exactly as the native runtime writes it: integers in
    ///     decimal, Booleans as words, and floating point as C's <c>%.17g</c>.
    /// </summary>
    public static string Format(object? value) =>
        value switch {
            long n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            double d => FormatDouble(d),
            string s => s,
            _ => "?",
        };

    static string FormatDouble(double value) {
        if (double.IsNaN(value)) {
            return "nan";
        }

        if (double.IsInfinity(value)) {
            return value > 0 ? "inf" : "-inf";
        }

        // .NET's G17 is C's %.17g but for the exponent's case and padding:
        // C writes at least two exponent digits, 'e+05'.
        var text = value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture).Replace('E', 'e');
        var e = text.IndexOf('e', StringComparison.Ordinal);
        if (e >= 0) {
            var sign = text[e + 1];
            var digits = text[(e + 2)..];
            text = text[..(e + 1)] + sign + digits.PadLeft(2, '0');
        }

        return text;
    }

    static object Wrap(Func<long> operation) {
        try {
            return checked(operation());
        } catch (OverflowException) {
            throw new SilTrap("integer overflow");
        }
    }
}
