using System.Globalization;
using System.Text;

namespace Sable.SIL;

/// <summary>A program stopped where the language says it must: overflow, division by zero, unreachable code.</summary>
public sealed class SilTrap(string message) : Exception(message);

/// <summary>
///     Runs SIL directly, so that tests can check what lowered code
///     <i>computes</i> — and what it does with memory — independently of the
///     LLVM backend.
/// </summary>
/// <remarks>
///     <para>
///         Values are <see cref="long" /> for <c>Int</c>, <see cref="bool" />
///         for <c>Bool</c>, <see cref="double" /> for <c>F64</c>, a
///         <see cref="Rune" /> for <c>Char</c>, an array of parts for a struct
///         or tuple, an <see cref="EnumValue" />, a storage reference for an
///         address, and a function's name for a function value. Integer
///         arithmetic traps on overflow and on division by zero, which is what
///         the generated code does. A step limit and a depth limit turn a
///         runaway program into a trap rather than a hung test.
///     </para>
///     <para>
///         Strings, objects and arrays are counted heap objects, as they are
///         natively. Every retain and release is carried out, so a program
///         that uses a freed object, releases one too often or leaves storage
///         holding a value as it is freed traps, and one that leaks leaves
///         <see cref="LiveObjects" /> above zero.
///     </para>
/// </remarks>
public sealed class SilInterpreter(SilModule module, int stepLimit = 10_000_000, int depthLimit = 2_000) {
    int _steps;
    readonly HashSet<Counted> _live = [];
    readonly Dictionary<StringLiteralInst, HeapString> _literals = [];

    /// <summary>Where <c>print</c> writes: one line per call, as the native runtime does.</summary>
    public StringBuilder Output { get; } = new();

    /// <summary>Heap objects allocated and not yet freed: after a whole program, a leak.</summary>
    public int LiveObjects => _live.Count;

    // =======================================================================
    // The heap
    // =======================================================================

    /// <summary>A counted heap object: a string, a class instance or an array buffer.</summary>
    abstract class Counted {
        public int Strong = 1;
        public bool Immortal;
        public bool Freed;
    }

    sealed class HeapString(string value) : Counted {
        public string Value { get; } = value;
    }

    sealed class HeapObject(ClassType type) : Counted {
        public ClassType Type { get; } = type;
        public object?[] Fields { get; } = new object?[type.Fields.Count];
    }

    sealed class HeapArray(ArrayType type, List<object?> elements) : Counted {
        public ArrayType Type { get; } = type;
        public List<object?> Elements { get; } = elements;
    }

    T Allocate<T>(T value) where T : Counted {
        _live.Add(value);
        return value;
    }

    static T Alive<T>(T value) where T : Counted =>
        value.Freed ? throw new SilTrap("use of a freed object") : value;

    HeapString Text(object? value) => Alive((HeapString)value!);

    /// <summary>A copy of a value: every reference it holds retained.</summary>
    void Retain(SilType type, object? value) {
        if (value is null || type.IsTrivial) {
            return;
        }

        switch (type, value) {
            case (_, Counted counted):
                if (!Alive(counted).Immortal) {
                    counted.Strong++;
                }

                return;

            case (ThickFunctionType, ClosureValue closure):
                Retain(SilType.Object, closure.Context);
                return;

            case (StructType structType, object?[] fields):
                for (var i = 0; i < fields.Length; i++) {
                    Retain(structType.Fields[i].Type, fields[i]);
                }

                return;

            case (TupleType tuple, object?[] elements):
                for (var i = 0; i < elements.Length; i++) {
                    Retain(tuple.Elements[i], elements[i]);
                }

                return;

            case (EnumType enumType, EnumValue enumValue):
                if (enumType.Cases[enumValue.Case].Payload is { } payload) {
                    Retain(payload, enumValue.Payload);
                }

                return;
        }
    }

    /// <summary>The end of a copy's life: every reference it holds released, and freed when the last goes.</summary>
    void Release(SilType type, object? value, int depth) {
        if (value is null || type.IsTrivial) {
            return;
        }

        switch (type, value) {
            case (ThickFunctionType, ClosureValue closure):
                Release(SilType.Object, closure.Context, depth);
                return;

            case (_, Counted counted):
                if (Alive(counted).Immortal) {
                    return;
                }

                if (--counted.Strong > 0) {
                    return;
                }

                Free(counted, depth);
                return;

            case (StructType structType, object?[] fields):
                for (var i = 0; i < fields.Length; i++) {
                    Release(structType.Fields[i].Type, fields[i], depth);
                }

                return;

            case (TupleType tuple, object?[] elements):
                for (var i = 0; i < elements.Length; i++) {
                    Release(tuple.Elements[i], elements[i], depth);
                }

                return;

            case (EnumType enumType, EnumValue enumValue):
                if (enumType.Cases[enumValue.Case].Payload is { } payload) {
                    Release(payload, enumValue.Payload, depth);
                }

                return;
        }
    }

    void Free(Counted counted, int depth) {
        switch (counted) {
            case HeapObject instance:
                // The deinit sees the object alive; it may not keep it.
                if (instance.Type.Deinit is { } deinit) {
                    counted.Strong = 1;
                    var function = module.Find(deinit) ?? throw new SilTrap($"call to undefined @{deinit}");
                    Call(function, [instance], depth + 1);
                    if (counted.Strong != 1) {
                        throw new SilTrap("an object outlived its deinit");
                    }
                }

                for (var i = instance.Fields.Length - 1; i >= 0; i--) {
                    Release(instance.Type.Fields[i].Type, instance.Fields[i], depth);
                }

                break;

            case HeapArray array:
                foreach (var element in array.Elements) {
                    Release(array.Type.Element, element, depth);
                }

                break;
        }

        counted.Freed = true;
        _live.Remove(counted);
    }

    // =======================================================================
    // Storage
    // =======================================================================

    /// <summary>An address: something that can be read and written.</summary>
    interface IReference {
        object? Get();
        void Set(object? value);

        /// <summary>The value, or null when the storage is empty.</summary>
        object? Peek();
    }

    /// <summary>An <c>alloc_stack</c>.</summary>
    sealed class Slot : IReference {
        object? _value;
        public object? Get() => _value ?? throw new SilTrap("load from uninitialised storage");
        public void Set(object? value) => _value = value;
        public object? Peek() => _value;
    }

    /// <summary>
    ///     A field or element inside an aggregate at another address. Writing
    ///     replaces the whole aggregate with a changed copy, so that a struct
    ///     value someone else holds never changes under them.
    /// </summary>
    /// <remarks>
    ///     An initialiser fills an empty aggregate one field at a time, so
    ///     writing into one that holds nothing yet makes it; reading a part
    ///     that was never written traps.
    /// </remarks>
    sealed class Element(IReference parent, int index, int count) : IReference {
        public object? Get() => Peek() ?? throw new SilTrap("load from uninitialised storage");

        public object? Peek() => parent.Peek() is object?[] whole ? whole[index] : null;

        public void Set(object? value) {
            var copy = parent.Peek() is object?[] whole ? (object?[])whole.Clone() : new object?[count];
            copy[index] = value;
            parent.Set(copy);
        }
    }

    /// <summary>A field of an object.</summary>
    sealed class Field(HeapObject instance, int index) : IReference {
        public object? Get() => Peek() ?? throw new SilTrap("load from uninitialised storage");
        public object? Peek() => Alive(instance).Fields[index];
        public void Set(object? value) => Alive(instance).Fields[index] = value;
    }

    /// <summary>An enum value: which case, and its payload.</summary>
    public sealed record EnumValue(int Case, object? Payload);

    /// <summary>A closure: its code, and the context it captured, or null.</summary>
    sealed record ClosureValue(string Function, object? Context);

    /// <summary>What a throwing call gives back instead of a result.</summary>
    sealed record Thrown(object? Error);

    // =======================================================================
    // Running
    // =======================================================================

    /// <summary>
    ///     Runs a function and gives its result, a string as a .NET string.
    ///     The result is released, so that <see cref="LiveObjects" /> counts
    ///     only what the program leaked.
    /// </summary>
    public object? Run(string function, params object[] arguments) {
        if (module.Find(function) is not { } target) {
            throw new ArgumentException($"no function @{function}", nameof(function));
        }

        _steps = 0;
        var passed = arguments.Select(static a => a is string s ? new HeapString(s) { Immortal = true } : a).ToArray();
        var result = Call(target, passed, 0);
        if (result is Thrown thrown) {
            throw new SilTrap($"uncaught error: {Format(thrown.Error)}");
        }

        var plain = result is HeapString text ? Alive(text).Value : result;
        Release(target.Type.Result, result, 0);
        return plain;
    }

    object? Call(SilFunction function, IReadOnlyList<object?> arguments, int depth) {
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
                        values[literal] = literal.Type == SilType.Bool ? literal.Value != 0
                            : literal.Type == SilType.Char ? new Rune((int)literal.Value)
                            : literal.Value;
                        break;

                    case FloatLiteralInst literal:
                        values[literal] = literal.Value;
                        break;

                    case StringLiteralInst literal:
                        if (!_literals.TryGetValue(literal, out var immortal)) {
                            _literals[literal] = immortal = new HeapString(literal.Value) { Immortal = true };
                        }

                        values[literal] = immortal;
                        break;

                    case BuiltinInst { Name: var name } builtin when name.StartsWith("print_", StringComparison.Ordinal):
                        Output.Append(Format(Get(builtin.Operands[0]))).Append('\n');
                        break;

                    case BuiltinInst builtin:
                        values[builtin] = Builtin(builtin, [..builtin.Operands.Select(Get)], depth);
                        break;

                    case AllocStackInst alloc:
                        values[alloc] = new Slot();
                        break;

                    case DeallocStackInst dealloc:
                        if (!((AllocStackInst)dealloc.Address).ElementType.IsTrivial && ((IReference)Get(dealloc.Address)!).Peek() is not null) {
                            throw new SilTrap("dealloc_stack of storage still holding a value");
                        }

                        break;

                    case LoadInst load: {
                        var address = (IReference)Get(load.Address)!;

                        // Empty storage for an array is the empty array.
                        var loaded = load.Type is ArrayType ? address.Peek() : address.Get();
                        switch (load.Kind) {
                            case LoadKind.Copy:
                                Retain(load.Type, loaded);
                                break;
                            case LoadKind.Take:
                                address.Set(null);
                                break;
                        }

                        values[load] = loaded;
                        break;
                    }

                    case StoreInst store: {
                        var address = (IReference)Get(store.Address)!;
                        var old = address.Peek();
                        if (!store.Value.Type.IsTrivial && store.Kind == StoreKind.Init && old is not null) {
                            throw new SilTrap("store [init] into storage that holds a value");
                        }

                        address.Set(Get(store.Value));
                        if (store.Kind == StoreKind.Assign) {
                            Release(store.Value.Type, old, depth);
                        }

                        break;
                    }

                    case DestroyAddrInst destroy: {
                        var address = (IReference)Get(destroy.Address)!;
                        var old = address.Peek();
                        address.Set(null);
                        Release(((AddressType)destroy.Address.Type).Pointee, old, depth);
                        break;
                    }

                    case CopyValueInst copy:
                        Retain(copy.Type, Get(copy.Value));
                        values[copy] = Get(copy.Value);
                        break;

                    case DestroyValueInst destroy:
                        Release(destroy.Value.Type, Get(destroy.Value), depth);
                        break;

                    case AllocRefInst alloc:
                        values[alloc] = Allocate(new HeapObject(alloc.ClassType));
                        break;

                    case RefElementAddrInst element:
                        values[element] = new Field(Alive((HeapObject)Get(element.Reference)!), element.Field);
                        break;

                    case StructInst or TupleInst:
                        values[instruction] = instruction.Operands.Select(Get).ToArray();
                        break;

                    case StructExtractInst extract:
                        values[extract] = ((object?[])Get(extract.Value)!)[extract.Field];
                        break;

                    case TupleExtractInst extract:
                        values[extract] = ((object?[])Get(extract.Value)!)[extract.Element];
                        break;

                    case StructElementAddrInst element: {
                        var structType = (StructType)((AddressType)element.Address.Type).Pointee;
                        values[element] = new Element((IReference)Get(element.Address)!, element.Field, structType.Fields.Count);
                        break;
                    }

                    case TupleElementAddrInst element: {
                        var tupleType = (TupleType)((AddressType)element.Address.Type).Pointee;
                        values[element] = new Element((IReference)Get(element.Address)!, element.Element, tupleType.Elements.Length);
                        break;
                    }

                    case EnumInst enumInst:
                        values[enumInst] = new EnumValue(enumInst.Case, enumInst.Payload is { } payload ? Get(payload) : null);
                        break;

                    case SwitchEnumInst switchEnum: {
                        // Storage that was never written reads as the first case, as zeroed memory does natively.
                        var scrutinee = (EnumValue?)Get(switchEnum.Value) ?? throw new SilTrap("switch_enum on an empty value");
                        var match = switchEnum.Cases.FirstOrDefault(c => c.Case == scrutinee.Case);
                        if (match.Target is { } target) {
                            next = target;
                            incoming = target.Arguments.Count == 0 ? [] : [scrutinee.Payload];
                        } else {
                            next = switchEnum.Default ?? throw new SilTrap("switch_enum matched no case");
                            incoming = [];
                        }

                        break;
                    }

                    case CondFailInst condFail:
                        if ((bool)Get(condFail.Condition)!) {
                            throw new SilTrap(condFail.Message);
                        }

                        break;

                    case ThrowInst throwInst:
                        return new Thrown(Get(throwInst.Error));

                    case TryApplyInst tryApply: {
                        var (callee, passed) = Callee(Get(tryApply.Callee)!, tryApply.Arguments.Select(Get));
                        var result = Call(callee, passed, depth + 1);
                        if (result is Thrown error) {
                            next = tryApply.Error;
                            incoming = [error.Error];
                        } else {
                            next = tryApply.Normal;
                            incoming = tryApply.Normal.Arguments.Count == 0 ? [] : [result];
                        }

                        break;
                    }

                    case FunctionRefInst reference:
                        values[reference] = reference.Function;
                        break;

                    case PartialApplyInst partial:
                        values[partial] = new ClosureValue((string)Get(partial.Function)!, partial.Context is { } context ? Get(context) : null);
                        break;

                    case UncheckedRefCastInst cast:
                        values[cast] = Get(cast.Value);
                        break;

                    case ApplyInst apply: {
                        var (callee, passed) = Callee(Get(apply.Callee)!, apply.Arguments.Select(Get));
                        values[apply] = Call(callee, passed, depth + 1);
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

    /// <summary>What a call runs: a function by name, or a closure's code with its context last.</summary>
    (SilFunction Function, object?[] Arguments) Callee(object callee, IEnumerable<object?> arguments) {
        var (name, context, hasContext) = callee switch {
            ClosureValue closure => (closure.Function, closure.Context, true),
            _ => ((string)callee, null, false),
        };

        var function = module.Find(name) ?? throw new SilTrap($"call to undefined @{name}");
        var passed = arguments.ToList();
        if (hasContext) {
            passed.Add(context);
        }

        return (function, passed.ToArray());
    }

    // =======================================================================
    // Builtins
    // =======================================================================

    object? Builtin(BuiltinInst builtin, object?[] operands, int depth) {
        switch (builtin.Name) {
            case "array_empty":
                return null;

            case "string_concat":
                return Allocate(new HeapString(Text(operands[0]).Value + Text(operands[1]).Value));

            case "string_count":
                return (long)Scalars(Text(operands[0]).Value).Count;

            case "string_at": {
                var scalars = Scalars(Text(operands[0]).Value);
                var index = (long)operands[1]!;
                return index >= 0 && index < scalars.Count ? scalars[(int)index] : throw new SilTrap("string index out of range");
            }

            case "string_slice": {
                var scalars = Scalars(Text(operands[0]).Value);
                var (from, to) = ((long)operands[1]!, (long)operands[2]!);
                if (from < 0 || to > scalars.Count || from > to) {
                    throw new SilTrap("string slice out of range");
                }

                return Allocate(new HeapString(string.Concat(scalars.Skip((int)from).Take((int)(to - from)))));
            }

            case "string_contains":
                return Scalars(Text(operands[0]).Value).Contains((Rune)operands[1]!);

            case "string_index_of":
                return (long)Scalars(Text(operands[0]).Value).IndexOf((Rune)operands[1]!);

            case "string_trimmed": {
                var scalars = Scalars(Text(operands[0]).Value);
                var start = 0;
                var end = scalars.Count;
                while (start < end && IsWhitespace(scalars[start])) {
                    start++;
                }

                while (end > start && IsWhitespace(scalars[end - 1])) {
                    end--;
                }

                return Allocate(new HeapString(string.Concat(scalars.Skip(start).Take(end - start))));
            }

            case "string_split": {
                var separator = ((Rune)operands[1]!).ToString();
                var parts = Text(operands[0]).Value.Split(separator);
                return Allocate(new HeapArray(new ArrayType(SilType.String), [..parts.Select(p => (object?)Allocate(new HeapString(p)))]));
            }

            case "string_from_Char":
                return Allocate(new HeapString(((Rune)operands[0]!).ToString()));

            case "string_from_Int" or "string_from_F64" or "string_from_Bool":
                return Allocate(new HeapString(Format(operands[0])));

            case "string_to_Int": {
                var text = Text(operands[0]).Value;
                return IsInteger(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                    ? new object?[] { true, value }
                    : new object?[] { false, 0L };
            }

            case "string_to_F64": {
                var text = Text(operands[0]).Value;
                return IsDecimal(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? new object?[] { true, value }
                    : new object?[] { false, 0.0 };
            }

            case "cmp_eq_String":
                return Text(operands[0]).Value == Text(operands[1]).Value;

            case "cmp_ne_String":
                return Text(operands[0]).Value != Text(operands[1]).Value;

            case "char_is_digit":
                return (Rune)operands[0]! is { Value: >= '0' and <= '9' };

            case "char_is_letter":
                return (Rune)operands[0]! is { Value: >= 'a' and <= 'z' or >= 'A' and <= 'Z' };

            case "char_is_whitespace":
                return IsWhitespace((Rune)operands[0]!);

            case "array_count":
                return operands[0] is HeapArray counted ? (long)Alive(counted).Elements.Count : 0L;

            case "array_get": {
                var array = operands[0] is HeapArray held ? Alive(held) : null;
                var index = (long)operands[1]!;
                if (array is null || index < 0 || index >= array.Elements.Count) {
                    throw new SilTrap("array index out of range");
                }

                var element = array.Elements[(int)index];
                Retain(builtin.Type, element);
                return element;
            }

            case "array_append":
                Unique(builtin.Operands[0], (IReference)operands[0]!).Elements.Add(operands[1]);
                return null;

            case "array_set": {
                var array = Unique(builtin.Operands[0], (IReference)operands[0]!);
                var index = (long)operands[1]!;
                if (index < 0 || index >= array.Elements.Count) {
                    throw new SilTrap("array index out of range");
                }

                var old = array.Elements[(int)index];
                array.Elements[(int)index] = operands[2];
                Release(array.Type.Element, old, depth);
                return null;
            }

            case "array_remove_last": {
                var array = Unique(builtin.Operands[0], (IReference)operands[0]!);
                if (array.Elements.Count == 0) {
                    throw new SilTrap("removeLast of an empty array");
                }

                var last = array.Elements[^1];
                array.Elements.RemoveAt(array.Elements.Count - 1);
                return last;
            }

            case "array_clear": {
                var address = (IReference)operands[0]!;
                var old = address.Peek();
                address.Set(null);
                Release(((AddressType)builtin.Operands[0].Type).Pointee, old, depth);
                return null;
            }
        }

        if (builtin.Name.EndsWith("_Char", StringComparison.Ordinal) && operands is [Rune left, Rune right]) {
            return Builtin(builtin.Name.Replace("_Char", "_Int", StringComparison.Ordinal), [(long)left.Value, (long)right.Value]);
        }

        return Builtin(builtin.Name, operands);
    }

    /// <summary>The array at an address, made unique — copied if it is shared — so that it can be changed.</summary>
    HeapArray Unique(SilValue addressValue, IReference address) {
        var type = (ArrayType)((AddressType)addressValue.Type).Pointee;
        if (address.Peek() is not HeapArray array) {
            var fresh = Allocate(new HeapArray(type, []));
            address.Set(fresh);
            return fresh;
        }

        if (Alive(array).Strong == 1) {
            return array;
        }

        var copy = Allocate(new HeapArray(type, [..array.Elements]));
        foreach (var element in copy.Elements) {
            Retain(type.Element, element);
        }

        array.Strong--;
        address.Set(copy);
        return copy;
    }

    static List<Rune> Scalars(string text) => [..text.EnumerateRunes()];

    /// <summary>The whitespace the runtime knows: ASCII's.</summary>
    static bool IsWhitespace(Rune c) => c.Value is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';

    /// <summary>An optional sign and decimal digits: what <c>toInt</c> reads, and nothing else.</summary>
    static bool IsInteger(string text) {
        var i = text.Length > 0 && text[0] is '+' or '-' ? 1 : 0;
        return i < text.Length && text[i..].All(static c => c is >= '0' and <= '9');
    }

    /// <summary>A decimal number: sign, digits, a fraction, an exponent. No infinities, NaN or hex.</summary>
    public static bool IsDecimal(string text) {
        var i = 0;
        if (i < text.Length && text[i] is '+' or '-') {
            i++;
        }

        var digits = 0;
        while (i < text.Length && text[i] is >= '0' and <= '9') {
            i++;
            digits++;
        }

        if (i < text.Length && text[i] == '.') {
            i++;
            while (i < text.Length && text[i] is >= '0' and <= '9') {
                i++;
                digits++;
            }
        }

        if (digits == 0) {
            return false;
        }

        if (i < text.Length && text[i] is 'e' or 'E') {
            i++;
            if (i < text.Length && text[i] is '+' or '-') {
                i++;
            }

            var exponent = 0;
            while (i < text.Length && text[i] is >= '0' and <= '9') {
                i++;
                exponent++;
            }

            if (exponent == 0) {
                return false;
            }
        }

        return i == text.Length;
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
                ("abs_Int", [long a]) => Wrap(() => Math.Abs(a)),
                ("min_Int", [long a, long b]) => Math.Min(a, b),
                ("max_Int", [long a, long b]) => Math.Max(a, b),

                ("add_F64", [double a, double b]) => a + b,
                ("sub_F64", [double a, double b]) => a - b,
                ("mul_F64", [double a, double b]) => a * b,
                ("div_F64", [double a, double b]) => a / b,
                ("neg_F64", [double a]) => -a,
                ("abs_F64", [double a]) => Math.Abs(a),

                // C's fmin and fmax: a NaN operand gives the other one.
                ("min_F64", [double a, double b]) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Min(a, b),
                ("max_F64", [double a, double b]) => double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b),

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

                ("convert_Int_F64", [long a]) => (double)a,
                ("convert_F64_Int", [double a]) => a is >= -9.2233720368547758e18 and < 9.2233720368547758e18
                    ? (long)a
                    : throw new SilTrap("F64 does not fit in Int"),

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
            long n => n.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            double d => FormatDouble(d),
            string s => s,
            HeapString s => Alive(s).Value,
            Rune c => c.ToString(),
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
        var text = value.ToString("G17", CultureInfo.InvariantCulture).Replace('E', 'e');
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
