using System.Collections.Immutable;

namespace Sable.SIL;

// ===========================================================================
// Types
// ===========================================================================

/// <summary>
///     A SIL type: what a value is at the level of storage and calling
///     convention, after the language's types have been lowered.
/// </summary>
/// <remarks>
///     Scalars, addresses, functions, tuples, structs, enums, classes and
///     arrays. Written with a leading <c>$</c> in SIL text: <c>$Int</c>,
///     <c>$*Int</c>, <c>$(Int, Bool) -&gt; Int</c>, <c>$[String]</c>.
/// </remarks>
public abstract class SilType : IEquatable<SilType> {
    public static readonly SilType Int = new ScalarType("Int");
    public static readonly SilType Bool = new ScalarType("Bool");
    public static readonly SilType F64 = new ScalarType("F64");
    public static readonly SilType F32 = new ScalarType("F32");
    public static readonly SilType F16 = new ScalarType("F16");

    // The sized integers (docs/03 §3.1): the widths inline layout stores.
    public static readonly SilType I8 = new ScalarType("I8");
    public static readonly SilType I16 = new ScalarType("I16");
    public static readonly SilType I32 = new ScalarType("I32");
    public static readonly SilType I64 = new ScalarType("I64");
    public static readonly SilType U8 = new ScalarType("U8");
    public static readonly SilType U16 = new ScalarType("U16");
    public static readonly SilType U32 = new ScalarType("U32");
    public static readonly SilType U64 = new ScalarType("U64");
    public static readonly SilType UInt = new ScalarType("UInt");
    public static readonly SilType I128 = new ScalarType("I128");
    public static readonly SilType U128 = new ScalarType("U128");

    /// <summary>Every integer type, <c>Int</c> first.</summary>
    public static readonly ImmutableArray<SilType> Integers = [Int, I8, I16, I32, I64, U8, U16, U32, U64, UInt, I128, U128];

    /// <summary>The floating-point types, widest first.</summary>
    public static readonly ImmutableArray<SilType> Floats = [F64, F32, F16];

    /// <summary>The integer and floating-point types.</summary>
    public static readonly ImmutableArray<SilType> Numbers = [..Integers, ..Floats];

    /// <summary>An integer type's width in bits and whether it is signed; null for anything else.</summary>
    public static (int Bits, bool Signed)? IntegerInfo(SilType type) =>
        (type as ScalarType)?.Name switch {
            "Int" or "I64" => (64, true),
            "I8" => (8, true),
            "I16" => (16, true),
            "I32" => (32, true),
            "U8" => (8, false),
            "U16" => (16, false),
            "U32" => (32, false),
            "U64" or "UInt" => (64, false),
            "I128" => (128, true),
            "U128" => (128, false),
            _ => null,
        };

    public static bool IsInteger(SilType type) => IntegerInfo(type) is not null;
    public static bool IsFloat(SilType type) => type == F64 || type == F32 || type == F16;

    /// <summary>A floating-point type's width in bits; null for anything else.</summary>
    public static int? FloatBits(SilType type) =>
        type == F64 ? 64 : type == F32 ? 32 : type == F16 ? 16 : null;

    /// <summary>One Unicode scalar value.</summary>
    public static readonly SilType Char = new ScalarType("Char");

    /// <summary>
    ///     A string: a reference to a counted heap object holding UTF-8 bytes.
    ///     A literal is an immortal object (docs/02 §2.2), whose count is never
    ///     touched; a built one is freed when its last reference goes.
    /// </summary>
    public static readonly SilType String = new ScalarType("String", trivial: false);

    /// <summary>
    ///     A reference to some counted object, of a type not known here: a
    ///     closure's context, as the closure's implementation receives it.
    ///     It may be null, for a closure that captured nothing.
    /// </summary>
    public static readonly SilType Object = new ScalarType("Object", trivial: false);

    /// <summary>
    ///     A region (docs/02 §2.1): a counted arena that values are
    ///     bump-allocated into and released with all at once, when its last
    ///     reference goes — at the end of its <c>region</c> block.
    /// </summary>
    public static readonly SilType Region = new ScalarType("Region", trivial: false);

    /// <summary><c>()</c>: no value. What a function that returns nothing returns.</summary>
    public static readonly SilType Unit = new ScalarType("()");

    public static SilType Address(SilType pointee) => new AddressType(pointee);

    public static SilType Function(ImmutableArray<SilType> parameters, SilType result) =>
        new FunctionType(parameters, result);

    public static SilType Tuple(ImmutableArray<SilType> elements) =>
        elements.IsEmpty ? Unit : new TupleType(elements);

    /// <summary>True for a value with no ownership to track: copied by copying its bits.</summary>
    public virtual bool IsTrivial => true;

    public abstract override string ToString();

    public bool Equals(SilType? other) => other is not null && ToString() == other.ToString();
    public override bool Equals(object? obj) => Equals(obj as SilType);
    public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(SilType? a, SilType? b) => a?.Equals(b) ?? b is null;
    public static bool operator !=(SilType? a, SilType? b) => !(a == b);
}

public sealed class ScalarType(string name, bool trivial = true) : SilType {
    public string Name { get; } = name;
    public override bool IsTrivial => trivial;
    public override string ToString() => Name;
}

/// <summary>
///     <c>[T]</c>: a growable array with value semantics — a reference to a
///     counted buffer, copied on write when shared. The empty array is no
///     buffer at all.
/// </summary>
public sealed class ArrayType(SilType element) : SilType {
    public SilType Element { get; } = element;
    public override bool IsTrivial => false;
    public override string ToString() => $"[{Element}]";
}

/// <summary><c>*T</c>: the address of storage holding a <c>T</c>.</summary>
public sealed class AddressType(SilType pointee) : SilType {
    public SilType Pointee { get; } = pointee;
    public override string ToString() => $"*{Pointee}";
}

/// <remarks>
///     A parameter is guaranteed — borrowed for the call — unless marked
///     <c>@owned</c>: then the caller hands it over, and the callee must
///     consume it. An initialiser's are owned, since it usually stores them.
/// </remarks>
public sealed class FunctionType(ImmutableArray<SilType> parameters, SilType result, SilType? thrown = null, ImmutableArray<bool> owned = default)
    : SilType {
    public ImmutableArray<SilType> Parameters { get; } = parameters;
    public SilType Result { get; } = result;

    /// <summary>The error a <c>throws(E)</c> function may throw, or null when it cannot.</summary>
    public SilType? Thrown { get; } = thrown;

    /// <summary>For each parameter, whether it is owned; empty when none is.</summary>
    public ImmutableArray<bool> Owned { get; } = owned.IsDefault || !owned.Contains(true) ? [] : owned;

    public bool IsOwned(int parameter) => !Owned.IsEmpty && Owned[parameter];

    public override string ToString() {
        var parameters = string.Join(", ", Parameters.Select((p, i) => IsOwned(i) ? $"@owned {p}" : p.ToString()));
        return Thrown is null ? $"({parameters}) -> {Result}" : $"({parameters}) throws({Thrown}) -> {Result}";
    }
}

/// <summary>
///     <c>@thick (Int) -&gt; Int</c>: a function as a value of the language —
///     code and a counted context holding what it captured. Calling one calls
///     the code with the arguments and then the context, so its code has the
///     <see cref="Implementation" /> type: the same with an <c>Object</c> last.
/// </summary>
public sealed class ThickFunctionType(FunctionType signature) : SilType {
    public FunctionType Signature { get; } = signature;

    public FunctionType Implementation =>
        new([..Signature.Parameters, Object], Signature.Result, Signature.Thrown, Signature.Owned.IsEmpty ? default : [..Signature.Owned, false]);

    public override bool IsTrivial => false;
    public override string ToString() => $"@thick {Signature}";
}

/// <summary>
///     <c>@sil_weak Optional$Node</c>: what a <c>weak</c> field holds — a
///     reference that does not keep its object alive and reads as none once
///     the object is gone. Only storage has this type; reading it with
///     <c>load_weak</c> gives the optional.
/// </summary>
public sealed class WeakStorageType(EnumType optional) : SilType {
    public EnumType Optional { get; } = optional;
    public ClassType Referent => (ClassType)Optional.Cases[Optional.CaseIndex("Some")].Payload!;
    public override bool IsTrivial => false;
    public override string ToString() => $"@sil_weak {Optional}";
}

/// <summary>
///     <c>@sil_unowned Node</c>: what an <c>unowned</c> field holds — a
///     reference that does not keep its object alive; reading it with
///     <c>load_unowned</c> once the object is gone traps.
/// </summary>
public sealed class UnownedStorageType(ClassType referent) : SilType {
    public ClassType Referent { get; } = referent;
    public override bool IsTrivial => false;
    public override string ToString() => $"@sil_unowned {Referent}";
}

/// <summary><c>(Int, Bool)</c>: a tuple, laid out inline. The empty tuple is <see cref="SilType.Unit" />.</summary>
public sealed class TupleType(ImmutableArray<SilType> elements) : SilType {
    public ImmutableArray<SilType> Elements { get; } = elements;
    public override bool IsTrivial => Elements.All(static e => e.IsTrivial);
    public override string ToString() => $"({string.Join(", ", Elements)})";
}

/// <summary>A named type a module declares: a struct, an enum or a class.</summary>
public abstract class NominalType(string name) : SilType {
    public string Name { get; } = name;

    /// <summary>A move-only struct or enum: a value of it is never copied, so it has ownership even when its parts are trivial.</summary>
    public bool IsNoncopyable { get; set; }

    public override string ToString() => Name;
}

/// <summary>
///     A struct: named fields laid out inline, in order. Declared in SIL text
///     as <c>sil_struct Point { x: $Int, y: $Int }</c>.
/// </summary>
public sealed class StructType(string name) : NominalType(name) {
    public List<(string Name, SilType Type)> Fields { get; } = [];

    public int FieldIndex(string field) => Fields.FindIndex(f => f.Name == field);

    /// <summary>A move-only struct's deinit, run as a value of it is destroyed, before its fields; or null.</summary>
    public string? Deinit { get; set; }

    /// <summary>
    ///     <c>@inline_layout</c> (docs/02 §2.5): a value of it is one
    ///     contiguous, pointer-free block of bytes, held in a counted buffer
    ///     that is copied on write — its fields are read and written in place,
    ///     at the offsets <see cref="InlineLayout"/> gives, never as SSA values.
    /// </summary>
    public bool IsInline { get; set; }

    public override bool IsTrivial => !IsInline && !IsNoncopyable && Fields.All(static f => f.Type.IsTrivial);
}

/// <summary>
///     An inline array field of an <c>@inline_layout</c> struct: <c>T[..k]</c>,
///     bounded at <c>k</c>, or <c>T[&gt;..]</c>, grow-only. Its count and then its
///     elements are stored in place; there is no SSA value of it, only a
///     pointer to where it is.
/// </summary>
public sealed class InlineArrayType(SilType element, int? capacity) : SilType {
    public SilType Element { get; } = element;

    /// <summary>The bound of <c>T[..k]</c>; null for the grow-only kind.</summary>
    public int? Capacity { get; } = capacity;

    public override string ToString() => Capacity is { } k ? $"@inline_array({Element}, {k})" : $"@inline_array({Element})";
}

/// <summary>
///     A relative reference field, <c>T&amp;&lt;u32&gt;?</c>: a signed offset from the
///     field's own address to the start of a <c>T</c> in the same block,
///     2 or 4 bytes; 0 is null.
/// </summary>
public sealed class RelativeReferenceType(StructType target, int width, bool optional) : SilType {
    public StructType Target { get; } = target;
    public int Width { get; } = width;
    public bool Optional { get; } = optional;

    public override string ToString() => $"@relative{(Optional ? "_optional" : "")}({Target}, {Width})";
}

/// <summary>
///     A view into inline bytes: where an inline struct, an inline array or a
///     scalar of one is, inside some block. Like an address, it borrows: it
///     is valid while the block it points into is neither freed nor changed.
/// </summary>
public sealed class InlinePointerType(SilType pointee) : SilType {
    public SilType Pointee { get; } = pointee;
    public override string ToString() => $"*inline {Pointee}";
}

/// <summary>
///     How an <c>@inline_layout</c> struct is laid out: its fixed-size fields
///     first, packed in declaration order — the prefix, each at an offset
///     known here — and then its variable-size fields in declaration order,
///     each found by walking past the ones before it. Scalars are
///     little-endian; a bounded array's count is the fewest bytes that hold
///     its bound, a grow-only one's a LEB128 varint; a relative reference is
///     a signed offset from itself. The emitter writes this as a descriptor
///     the runtime walks; the interpreter walks it here.
/// </summary>
public sealed class InlineLayout {
    public const int ScalarKind = 0, BoolKind = 1, CharKind = 2, ReferenceKind = 3, StructKind = 4, BoundedKind = 5, GrowKind = 6;

    public sealed record Slot(
        int Field,
        string Name,
        SilType Type,
        int Offset,
        int Kind,
        int Width,
        bool Optional,
        int Capacity,
        int ElementKind,
        int ElementWidth,
        StructType? Layout
    ) {
        public bool IsFixed => Offset >= 0;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StructType, InlineLayout> Cache = new();

    InlineLayout(StructType type) {
        Type = type;
        var fixedSlots = new List<Slot>();
        var variableSlots = new List<Slot>();
        var offset = 0;
        for (var i = 0; i < type.Fields.Count; i++) {
            var (name, fieldType) = type.Fields[i];
            if (FixedSizeOf(fieldType) is { } size) {
                fixedSlots.Add(Describe(i, name, fieldType, offset));
                offset += size;
            } else {
                variableSlots.Add(Describe(i, name, fieldType, -1));
            }
        }

        Prefix = offset;
        Slots = [..fixedSlots, ..variableSlots];
        FixedSize = variableSlots.Count == 0 ? offset : null;
    }

    public StructType Type { get; }

    /// <summary>The fields in the order they are stored: fixed ones first.</summary>
    public ImmutableArray<Slot> Slots { get; }

    /// <summary>The bytes the fixed-size fields take, at the start.</summary>
    public int Prefix { get; }

    /// <summary>The size of every value of it, when that does not vary.</summary>
    public int? FixedSize { get; }

    public int SlotOf(int field) => Slots.IndexOf(Slots.First(s => s.Field == field));

    /// <summary>The empty value's size: every count zero, and so on down.</summary>
    public int EmptySize =>
        Prefix + Slots.Where(static s => !s.IsFixed).Sum(static s =>
            s.Kind switch {
                BoundedKind => s.Width,
                GrowKind => 1,
                _ => Of(s.Layout!).EmptySize,
            });

    /// <summary>Whether a value of it can hold a relative reference, here or nested.</summary>
    public bool HasReferences => HoldsReferences(this, []);

    static bool HoldsReferences(InlineLayout layout, HashSet<StructType> seen) {
        if (!seen.Add(layout.Type)) {
            return false;
        }

        return layout.Slots.Any(s => s.Kind == ReferenceKind || s.Layout is { } nested && HoldsReferences(Of(nested), seen));
    }

    public static InlineLayout Of(StructType type) => Cache.GetValue(type, static t => new InlineLayout(t));

    /// <summary>A scalar's kind and width, as stored inline; null for anything that is not one.</summary>
    public static (int Kind, int Width)? Scalar(SilType type) =>
        type == SilType.Bool ? (BoolKind, 1)
        : type == SilType.Char ? (CharKind, 4)
        : type == SilType.F64 ? (ScalarKind, 8)
        : type == SilType.F32 ? (ScalarKind, 4)
        : type == SilType.F16 ? (ScalarKind, 2)
        : SilType.IntegerInfo(type) is var (bits, _) ? (ScalarKind, bits / 8)
        : null;

    /// <summary>The bytes a value of the type always takes inline; null when that varies.</summary>
    public static int? FixedSizeOf(SilType type) =>
        type switch {
            _ when Scalar(type) is var (_, width) => width,
            RelativeReferenceType reference => reference.Width,
            StructType { IsInline: true } nested => Of(nested).FixedSize,
            _ => null,
        };

    /// <summary>The fewest bytes that hold a bounded array's count.</summary>
    public static int CountWidth(int capacity) => capacity < 256 ? 1 : capacity < 65536 ? 2 : 4;

    /// <summary>An inline array's description, as the runtime walks one: its count, its elements.</summary>
    public static Slot DescribeArray(InlineArrayType array) => Describe(-1, "", array, -1);

    static Slot Describe(int field, string name, SilType type, int offset) {
        switch (type) {
            case RelativeReferenceType reference:
                return new(field, name, type, offset, ReferenceKind, reference.Width, reference.Optional, 0, 0, 0, reference.Target);

            case StructType { IsInline: true } nested:
                return new(field, name, type, offset, StructKind, 0, false, 0, 0, 0, nested);

            case InlineArrayType array: {
                var (elementKind, elementWidth, layout) = Scalar(array.Element) is var (kind, width)
                    ? (kind, width, (StructType?)null)
                    : (StructKind, 0, (StructType)array.Element);
                return array.Capacity is { } capacity
                    ? new(field, name, type, offset, BoundedKind, CountWidth(capacity), false, capacity, elementKind, elementWidth, layout)
                    : new(field, name, type, offset, GrowKind, 0, false, 0, elementKind, elementWidth, layout);
            }

            default: {
                var (kind, width) = Scalar(type) ?? throw new InvalidOperationException($"{type} is not stored inline");
                return new(field, name, type, offset, kind, width, false, 0, 0, 0, null);
            }
        }
    }
}

/// <summary>
///     A tagged union: cases, each with at most one payload — several values
///     are one tuple. Unions, enums and optionals all lower to it. Declared as
///     <c>sil_enum Shape { Circle($F64), Empty }</c>.
/// </summary>
public sealed class EnumType(string name) : NominalType(name) {
    public List<(string Name, SilType? Payload)> Cases { get; } = [];

    /// <summary>
    ///     A union case's payload fields' names, by case, for a debugger to
    ///     show (<c>Rect(width: 2, height: 3)</c>); only for a case whose
    ///     fields all have one. The payload itself is the bare value or tuple.
    /// </summary>
    public Dictionary<string, ImmutableArray<string>> PayloadLabels { get; } = [];

    public int CaseIndex(string @case) => Cases.FindIndex(c => c.Name == @case);

    public override bool IsTrivial => !IsNoncopyable && Cases.All(static c => c.Payload?.IsTrivial ?? true);
}

/// <summary>
///     A class: a reference to a counted heap object with identity, whose
///     fields live in the object. Declared as
///     <c>sil_class Node { value: $Int, next: $Optional$Node }</c>; a field
///     may name the class itself, since it is reached through a reference.
/// </summary>
/// <remarks>
///     A subclass's fields start with its superclass's, in the same places,
///     so a field's index is the same in every class that has it. Its vtable
///     likewise starts with its superclass's methods, an override in its
///     base method's slot.
/// </remarks>
public sealed class ClassType(string name) : NominalType(name) {
    public List<(string Name, SilType Type)> Fields { get; } = [];

    public int FieldIndex(string field) => Fields.FindIndex(f => f.Name == field);

    public ClassType? Superclass { get; set; }

    /// <summary>The methods called through the vtable: each slot's name, and the function in it for this class.</summary>
    public List<(string Method, string Function)> VTable { get; } = [];

    public int MethodIndex(string method) => VTable.FindIndex(m => m.Method == method);

    /// <summary>The function that runs as an object dies, before its fields are destroyed, or null.</summary>
    public string? Deinit { get; set; }

    /// <summary>True when this class is the given one or inherits from it.</summary>
    public bool IsSubclassOf(ClassType other) {
        for (var type = this; type is not null; type = type.Superclass) {
            if (ReferenceEquals(type, other)) {
                return true;
            }
        }

        return false;
    }

    public override bool IsTrivial => false;
}

// ===========================================================================
// Values
// ===========================================================================

/// <summary>Something an instruction can use: a block argument, or an instruction's result.</summary>
public abstract class SilValue {
    public abstract SilType Type { get; }
}

/// <summary>
///     How a value is held (docs/04 §2.1). A trivial value has nothing to
///     track. An owned one must be consumed exactly once on every path — by a
///     store, a return, a branch, an aggregate taking it in, or
///     <c>destroy_value</c>. A guaranteed one is borrowed: it may be used, and
///     copied, while whatever it was borrowed from is alive, but never consumed.
/// </summary>
public enum Ownership {
    Trivial,
    Owned,
    Guaranteed,
}

/// <summary>A value passed into a block: a function's parameters, or what a branch sends.</summary>
/// <remarks>
///     A function's parameters are guaranteed: the caller keeps them alive for
///     the call. Other arguments are owned, except where a <c>switch_enum</c>
///     hands a block the payload it borrows from the enum, marked
///     <c>@guaranteed</c> in SIL text.
/// </remarks>
public sealed class SilArgument(SilBlock block, SilType type, bool guaranteed = false) : SilValue {
    public SilBlock Block { get; } = block;
    public override SilType Type { get; } = type;

    /// <summary>True for an argument that borrows rather than owns, outside the entry block.</summary>
    public bool IsGuaranteed { get; } = guaranteed;
}

// ===========================================================================
// Instructions
// ===========================================================================

/// <summary>
///     Where in the source an instruction came from: one-based line and
///     column, and the lexical scope it is in — 0 for the function's own,
///     otherwise one of <see cref="SilFunction.Scopes" />, counted from 1.
/// </summary>
public readonly record struct SilLocation(int Line, int Column, int Scope = 0) {
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>A block of a function's source that its locals can be declared in: the scope it is inside (0 for the function's), and where it starts.</summary>
public readonly record struct SilScope(int Parent, int Line, int Column);

/// <summary>
///     One SIL instruction. An instruction that produces a value is itself the
///     value, as in Swift's SIL, so a use names the instruction.
/// </summary>
public abstract class SilInstruction : SilValue {
    SilValue[] _operands;

    protected SilInstruction(params SilValue[] operands) {
        _operands = operands;
    }

    public SilBlock Block { get; internal set; } = null!;

    /// <summary>The source position it was lowered from, for debug information.</summary>
    public SilLocation? Location { get; set; }

    public IReadOnlyList<SilValue> Operands => _operands;

    internal void SetOperand(int index, SilValue value) => _operands[index] = value;

    /// <summary>
    ///     A copy, for another block — what the inliner makes of a callee's
    ///     body: the same operands, each passed through <paramref name="value" />,
    ///     and the same targets, through <paramref name="block" />.
    /// </summary>
    internal SilInstruction Clone(Func<SilValue, SilValue> value, Func<SilBlock, SilBlock> block) {
        var copy = this is SwitchEnumInst switchEnum
            ? new SwitchEnumInst(switchEnum.Value, [..switchEnum.Cases.Select(c => (c.Case, block(c.Target)))], switchEnum.Default is { } fallback ? block(fallback) : null)
            : (SilInstruction)MemberwiseClone();
        copy._operands = [.._operands.Select(value)];
        copy.Block = null!;
        copy.Location = Location;
        switch (copy) {
            case BranchInst branch:
                branch.Target = block(branch.Target);
                break;
            case CondBranchInst conditional:
                conditional.WhenTrue = block(conditional.WhenTrue);
                conditional.WhenFalse = block(conditional.WhenFalse);
                break;
            case TryApplyInst tryApply:
                tryApply.Normal = block(tryApply.Normal);
                tryApply.Error = block(tryApply.Error);
                break;
        }

        return copy;
    }

    /// <summary>The text that names the instruction in SIL: <c>apply</c>, <c>cond_br</c>.</summary>
    public abstract string Opcode { get; }

    /// <summary>True when the instruction produces a value that can be named.</summary>
    public virtual bool HasResult => true;

    /// <summary>True for an instruction that ends a block.</summary>
    public virtual bool IsTerminator => false;

    public override SilType Type => SilType.Unit;
}

/// <summary><c>integer_literal $Int, 42</c>, or <c>$Bool, 1</c> for true.</summary>
/// <remarks>
///     A 128-bit literal keeps all its bits in <see cref="WideValue" />, read
///     as signed: a <c>U128</c> past <c>I128</c>'s range is held as the
///     negative number with the same bits, as a <c>U64</c> is in <see cref="Value" />.
/// </remarks>
public sealed class IntegerLiteralInst : SilInstruction {
    public IntegerLiteralInst(SilType type, long value) : this(type, (Int128)value) { }

    public IntegerLiteralInst(SilType type, Int128 value) {
        Type = type;
        WideValue = value;
    }

    public override SilType Type { get; }

    /// <summary>The value's low 64 bits: all of it for any type narrower than 128 bits.</summary>
    public long Value => unchecked((long)WideValue);

    public Int128 WideValue { get; }
    public override string Opcode => "integer_literal";
}

/// <summary><c>float_literal $F64, 1.5</c>.</summary>
public sealed class FloatLiteralInst(double value, SilType? type = null) : SilInstruction {
    public override SilType Type { get; } = type ?? SilType.F64;
    public double Value { get; } = value;
    public override string Opcode => "float_literal";
}

/// <summary><c>string_literal "hello"</c>: an immortal string.</summary>
public sealed class StringLiteralInst(string value) : SilInstruction {
    public override SilType Type => SilType.String;
    public string Value { get; } = value;
    public override string Opcode => "string_literal";
}

/// <summary>
///     <c>builtin "add_Int"(%0, %1) : $Int</c>: an operation the compiler
///     knows, named in <see cref="SilBuiltins" />. One that gives <c>()</c>,
///     such as <c>print_String</c>, is run for its effect and names nothing.
/// </summary>
public sealed class BuiltinInst(string name, SilType type, params SilValue[] operands) : SilInstruction(operands) {
    public string Name { get; private set; } = name;

    /// <summary>The same operation, differently made: <c>inline_new</c> on the stack.</summary>
    public void Rename(string name) => Name = name;
    public override SilType Type { get; } = type;
    public override bool HasResult => Type != SilType.Unit;
    public override string Opcode => "builtin";
}

/// <summary><c>struct $Point (%0, %1)</c>: a struct from its fields, in order.</summary>
public sealed class StructInst(StructType type, params SilValue[] fields) : SilInstruction(fields) {
    public StructType StructType { get; } = type;
    public override SilType Type => StructType;
    public override string Opcode => "struct";
}

/// <summary><c>struct_extract %0, #x</c>: one field of a struct value.</summary>
public sealed class StructExtractInst(SilValue value, int field) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public int Field { get; } = field;
    public override SilType Type => ((StructType)Value.Type).Fields[Field].Type;
    public override string Opcode => "struct_extract";
}

/// <summary><c>struct_element_addr %0, #x</c>: the address of one field of a struct in memory.</summary>
public sealed class StructElementAddrInst(SilValue address, int field) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public int Field { get; } = field;
    public override SilType Type => SilType.Address(((StructType)((AddressType)Address.Type).Pointee).Fields[Field].Type);
    public override string Opcode => "struct_element_addr";
}

/// <summary><c>tuple $(Int, Bool) (%0, %1)</c>.</summary>
public sealed class TupleInst(TupleType type, params SilValue[] elements) : SilInstruction(elements) {
    public TupleType TupleType { get; } = type;
    public override SilType Type => TupleType;
    public override string Opcode => "tuple";
}

/// <summary><c>tuple_extract %0, 1</c>.</summary>
public sealed class TupleExtractInst(SilValue value, int element) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public int Element { get; } = element;
    public override SilType Type => ((TupleType)Value.Type).Elements[Element];
    public override string Opcode => "tuple_extract";
}

/// <summary><c>tuple_element_addr %0, 1</c>.</summary>
public sealed class TupleElementAddrInst(SilValue address, int element) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public int Element { get; } = element;
    public override SilType Type => SilType.Address(((TupleType)((AddressType)Address.Type).Pointee).Elements[Element]);
    public override string Opcode => "tuple_element_addr";
}

/// <summary><c>enum $Shape, #Circle, %0</c>, or without a payload for a case that has none.</summary>
public sealed class EnumInst(EnumType type, int @case, SilValue? payload) : SilInstruction(payload is null ? [] : [payload]) {
    public EnumType EnumType { get; } = type;
    public int Case { get; } = @case;
    public SilValue? Payload => Operands.Count == 0 ? null : Operands[0];
    public override SilType Type => EnumType;
    public override string Opcode => "enum";
}

/// <summary><c>cond_fail %0, "message"</c>: stops the program with the message when the condition is true.</summary>
public sealed class CondFailInst(SilValue condition, string message) : SilInstruction(condition) {
    public SilValue Condition => Operands[0];
    public string Message { get; } = message;
    public override bool HasResult => false;
    public override string Opcode => "cond_fail";
}

/// <summary><c>alloc_stack $T</c>: storage in the current frame, popped by <c>dealloc_stack</c>.</summary>
/// <remarks>
///     Storage for a non-trivial type starts <i>empty</i>, holding no value
///     and nothing to release: <c>store [assign]</c> into it destroys nothing,
///     and <c>destroy_addr</c> of it does nothing. So a slot can be assigned
///     on every path without knowing whether it was assigned before, and
///     destroyed once where the function leaves. Natively, empty is all zeros.
/// </remarks>
public sealed class AllocStackInst(SilType elementType) : SilInstruction {
    public SilType ElementType { get; } = elementType;

    /// <summary>The source variable the slot holds, for a debugger; null for a temporary.</summary>
    public string? VariableName { get; init; }

    /// <summary>
    ///     <c>alloc_stack [unmanaged]</c>: holds a reference without owning it —
    ///     stored without a copy, never destroyed — where something else
    ///     provably keeps it alive (<see cref="BorrowFields" />).
    /// </summary>
    public bool Unmanaged { get; set; }

    public override SilType Type => SilType.Address(ElementType);
    public override string Opcode => "alloc_stack";
}

/// <summary><c>dealloc_stack %0</c>: frees the most recent <c>alloc_stack</c>.</summary>
public sealed class DeallocStackInst(SilValue address) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override bool HasResult => false;
    public override string Opcode => "dealloc_stack";
}

public enum LoadKind {
    /// <summary>A trivial value: its bits.</summary>
    Trivial,

    /// <summary>A copy of the value, which the storage keeps.</summary>
    Copy,

    /// <summary>The value itself, leaving the storage empty.</summary>
    Take,
}

/// <summary><c>load [trivial|copy|take] %0</c>: reads the value at an address.</summary>
public sealed class LoadInst(SilValue address, LoadKind? kind = null) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override SilType Type => (Address.Type as AddressType)?.Pointee ?? SilType.Unit;

    /// <summary>Unless given, trivial for a trivial type and a copy otherwise.</summary>
    public LoadKind Kind { get; } = kind ?? ((address.Type as AddressType)?.Pointee.IsTrivial ?? true ? LoadKind.Trivial : LoadKind.Copy);

    public override string Opcode => "load";
}

public enum StoreKind {
    /// <summary>The storage held nothing before.</summary>
    Init,

    /// <summary>The storage may hold a value, which this destroys and replaces.</summary>
    Assign,
}

/// <summary><c>store %1 to [init] %0</c>.</summary>
public sealed class StoreInst(SilValue value, SilValue address, StoreKind kind) : SilInstruction(value, address) {
    public SilValue Value => Operands[0];
    public SilValue Address => Operands[1];
    public StoreKind Kind { get; } = kind;
    public override bool HasResult => false;
    public override string Opcode => "store";
}

/// <summary>
///     <c>load_borrow %0</c>: the value in storage, borrowed rather than
///     copied — no retain — until <c>end_borrow</c>. Nothing may write the
///     storage in between, which the verifier checks for a stack slot.
/// </summary>
public sealed class LoadBorrowInst(SilValue address) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override SilType Type => (Address.Type as AddressType)?.Pointee ?? SilType.Unit;
    public override string Opcode => "load_borrow";
}

/// <summary><c>end_borrow %1</c>: the end of a <c>load_borrow</c>.</summary>
public sealed class EndBorrowInst(SilValue borrowed) : SilInstruction(borrowed) {
    public SilValue Borrowed => Operands[0];
    public override bool HasResult => false;
    public override string Opcode => "end_borrow";
}

/// <summary><c>load_weak %0</c>: the object a weak reference holds, retained, or none if it is gone.</summary>
public sealed class LoadWeakInst(SilValue address) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override SilType Type => ((WeakStorageType)((AddressType)Address.Type).Pointee).Optional;
    public override string Opcode => "load_weak";
}

/// <summary><c>store_weak %1 to %0</c>: holds an optional's object weakly, borrowing the optional.</summary>
public sealed class StoreWeakInst(SilValue value, SilValue address) : SilInstruction(value, address) {
    public SilValue Value => Operands[0];
    public SilValue Address => Operands[1];
    public override bool HasResult => false;
    public override string Opcode => "store_weak";
}

/// <summary><c>load_unowned %0</c>: the object an unowned reference holds, retained; a trap if it is gone.</summary>
public sealed class LoadUnownedInst(SilValue address) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override SilType Type => ((UnownedStorageType)((AddressType)Address.Type).Pointee).Referent;
    public override string Opcode => "load_unowned";
}

/// <summary><c>store_unowned %1 to %0</c>: holds an object without keeping it alive, borrowing it.</summary>
public sealed class StoreUnownedInst(SilValue value, SilValue address) : SilInstruction(value, address) {
    public SilValue Value => Operands[0];
    public SilValue Address => Operands[1];
    public override bool HasResult => false;
    public override string Opcode => "store_unowned";
}

/// <summary><c>copy_value %0</c>: a new owned copy of a value — a retain, for a reference.</summary>
public sealed class CopyValueInst(SilValue value) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type => Value.Type;
    public override string Opcode => "copy_value";
}

/// <summary><c>destroy_value %0</c>: consumes an owned value — a release, for a reference.</summary>
public sealed class DestroyValueInst(SilValue value) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override bool HasResult => false;
    public override string Opcode => "destroy_value";
}

/// <summary><c>destroy_addr %0</c>: destroys the value in storage, leaving it empty.</summary>
public sealed class DestroyAddrInst(SilValue address) : SilInstruction(address) {
    public SilValue Address => Operands[0];
    public override bool HasResult => false;
    public override string Opcode => "destroy_addr";
}

/// <summary><c>alloc_ref $Node</c>: a new object, its count one and every field empty.</summary>
public sealed class AllocRefInst(ClassType type) : SilInstruction {
    public ClassType ClassType { get; } = type;

    /// <summary><c>alloc_ref [stack]</c>: made in the frame, which it does not outlive (docs/04 §3.7).</summary>
    public bool OnStack { get; set; }
    public override SilType Type => ClassType;
    public override string Opcode => "alloc_ref";
}

/// <summary><c>ref_element_addr %0, #next</c>: the address of a field inside an object.</summary>
public sealed class RefElementAddrInst(SilValue reference, int field) : SilInstruction(reference) {
    public SilValue Reference => Operands[0];
    public int Field { get; } = field;
    public override SilType Type => SilType.Address(((ClassType)Reference.Type).Fields[Field].Type);
    public override string Opcode => "ref_element_addr";
}

/// <summary>
///     <c>partial_apply %f(%context) : $@thick (Int) -&gt; Int</c>: a closure —
///     the code, whose last parameter takes the context, with the context
///     object, which it consumes. With no context, the closure captured
///     nothing and its context is null.
/// </summary>
public sealed class PartialApplyInst(SilValue function, SilValue? context, ThickFunctionType type)
    : SilInstruction(context is null ? [function] : [function, context]) {
    public SilValue Function => Operands[0];
    public SilValue? Context => Operands.Count > 1 ? Operands[1] : null;
    public override SilType Type { get; } = type;
    public ThickFunctionType ThickType => (ThickFunctionType)Type;
    public override string Opcode => "partial_apply";
}

/// <summary>
///     <c>unchecked_ref_cast %0 : $Context</c>: an <c>Object</c> seen as the
///     class it is known to be — a closure's context, inside its code. The
///     result is borrowed from the operand.
/// </summary>
public sealed class UncheckedRefCastInst(SilValue value, ClassType type) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type { get; } = type;
    public override string Opcode => "unchecked_ref_cast";
}

/// <summary>
///     <c>upcast %0 : $Base</c>: an object seen as one of its superclasses.
///     The result is borrowed from the operand; something that must own it
///     copies it.
/// </summary>
public sealed class UpcastInst(SilValue value, ClassType type) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type { get; } = type;
    public override string Opcode => "upcast";
}

/// <summary>
///     <c>class_method %0, #describe : $(Node) -&gt; String</c>: the code of a
///     method for the class the object actually is, from its vtable.
/// </summary>
public sealed class ClassMethodInst(SilValue reference, string method, FunctionType type) : SilInstruction(reference) {
    public SilValue Reference => Operands[0];
    public string Method { get; } = method;
    public override SilType Type { get; } = type;
    public override string Opcode => "class_method";
}

/// <summary>
///     <c>global_addr @M.total : $*Int</c>: where a global lives — a
///     module-level variable or a type's static stored property, for the
///     whole of the program.
/// </summary>
/// <summary>
///     Storage more than one part of a program can reach (docs/02 §2.3): a
///     global, or a field of an object — a class's, a closure's context, a
///     captured variable's box — whoever holds a reference to it. An access to
///     it the compiler cannot prove exclusive is checked as the program runs:
///     an inout or ref argument's for the length of its call, and a plain
///     read or write at the moment it happens. Storage on the stack is the
///     function's alone, and an address passed in is the caller's access.
/// </summary>
public static class SharedStorage {
    public static bool Contains(SilValue address) =>
        address switch {
            RefElementAddrInst or GlobalAddrInst => true,
            StructElementAddrInst element => Contains(element.Address),
            TupleElementAddrInst element => Contains(element.Address),
            _ => false,
        };

    /// <summary>
    ///     The plain read or write an instruction makes of storage, as it runs:
    ///     where, and whether it changes it. Null for one that makes none.
    /// </summary>
    public static (SilValue Address, bool Modify)? AccessOf(SilInstruction instruction) =>
        instruction switch {
            LoadInst load => (load.Address, load.Kind == LoadKind.Take),
            LoadBorrowInst borrow => (borrow.Address, false),
            StoreInst store => (store.Address, true),
            LoadWeakInst weak => (weak.Address, false),
            StoreWeakInst weak => (weak.Address, true),
            LoadUnownedInst unowned => (unowned.Address, false),
            StoreUnownedInst unowned => (unowned.Address, true),
            // An array changed where it is: its count, its buffer, an element.
            BuiltinInst { Name: "array_append" or "array_set" or "array_set_unique" or "array_make_unique" or "array_remove_last" or "array_clear" or "array_element_addr" } array
                => (array.Operands[0], true),
            _ => null,
        };

    /// <summary>The plain access an instruction makes of shared storage, which a checked build checks; null for none.</summary>
    public static (SilValue Address, bool Modify)? CheckedAccessOf(SilInstruction instruction) =>
        AccessOf(instruction) is { } access && Contains(access.Address) ? access : null;
}

public sealed class GlobalAddrInst(string global, SilType type) : SilInstruction {
    public string Global { get; } = global;
    public SilType ValueType { get; } = type;
    public override SilType Type => SilType.Address(ValueType);
    public override string Opcode => "global_addr";
}

/// <summary><c>function_ref @f : $(Int) -&gt; Int</c>: a function as a value.</summary>
public sealed class FunctionRefInst(string function, FunctionType type) : SilInstruction {
    public string Function { get; } = function;
    public override SilType Type { get; } = type;
    public override string Opcode => "function_ref";
}

/// <summary><c>apply %f(%0, %1) : $Int</c>: calls a function value.</summary>
public sealed class ApplyInst(SilValue callee, SilType resultType, params SilValue[] arguments)
    : SilInstruction([callee, ..arguments]) {
    public SilValue Callee => Operands[0];
    public IEnumerable<SilValue> Arguments => Operands.Skip(1);
    public override SilType Type { get; } = resultType;
    public override bool HasResult => Type != SilType.Unit;
    public override string Opcode => "apply";
}

/// <summary><c>br bb2(%0)</c>: jumps, passing values to the target's arguments.</summary>
public sealed class BranchInst(SilBlock target, params SilValue[] arguments) : SilInstruction(arguments) {
    public SilBlock Target { get; internal set; } = target;
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "br";
}

/// <summary><c>cond_br %c, bb1(%a), bb2</c>.</summary>
public sealed class CondBranchInst(
    SilValue condition,
    SilBlock whenTrue,
    SilValue[] trueArguments,
    SilBlock whenFalse,
    SilValue[] falseArguments) : SilInstruction([condition, ..trueArguments, ..falseArguments]) {
    public SilValue Condition => Operands[0];
    public SilBlock WhenTrue { get; internal set; } = whenTrue;
    public SilBlock WhenFalse { get; internal set; } = whenFalse;
    public int TrueArgumentCount { get; } = trueArguments.Length;
    public IEnumerable<SilValue> TrueArguments => Operands.Skip(1).Take(TrueArgumentCount);
    public IEnumerable<SilValue> FalseArguments => Operands.Skip(1 + TrueArgumentCount);
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "cond_br";
}

/// <summary><c>return %0</c>, or a bare <c>return</c> from a function returning <c>()</c>.</summary>
public sealed class ReturnInst(SilValue? value) : SilInstruction(value is null ? [] : [value]) {
    public SilValue? Value => Operands.Count == 0 ? null : Operands[0];
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "return";
}

/// <summary>
///     <c>switch_enum %0, case #A: bb1, case #B: bb2, default bb3</c>: branches
///     on an enum's case. A case block takes the payload as its one argument
///     when the case has one; the default block takes nothing.
/// </summary>
public sealed class SwitchEnumInst(SilValue value, List<(int Case, SilBlock Target)> cases, SilBlock? defaultTarget)
    : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public List<(int Case, SilBlock Target)> Cases { get; } = cases;
    public SilBlock? Default { get; internal set; } = defaultTarget;
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "switch_enum";
}

/// <summary><c>throw %0</c>: leaves a <c>throws</c> function with an error.</summary>
public sealed class ThrowInst(SilValue error) : SilInstruction(error) {
    public SilValue Error => Operands[0];
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "throw";
}

/// <summary>
///     <c>try_apply %f(%0) : $Int, normal bb1, error bb2</c>: calls a throwing
///     function. The normal block takes the result (nothing for <c>()</c>);
///     the error block takes the error.
/// </summary>
public sealed class TryApplyInst(SilValue callee, SilType resultType, SilValue[] arguments, SilBlock normal, SilBlock error)
    : SilInstruction([callee, ..arguments]) {
    public SilValue Callee => Operands[0];
    public IEnumerable<SilValue> Arguments => Operands.Skip(1);
    public SilType ResultType { get; } = resultType;
    public SilBlock Normal { get; internal set; } = normal;
    public SilBlock Error { get; internal set; } = error;
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "try_apply";
}

/// <summary><c>unreachable</c>: control cannot get here.</summary>
public sealed class UnreachableInst() : SilInstruction {
    public override bool HasResult => false;
    public override bool IsTerminator => true;
    public override string Opcode => "unreachable";
}

// ===========================================================================
// Blocks, functions, modules
// ===========================================================================

public sealed class SilBlock(SilFunction function) {
    public SilFunction Function { get; } = function;
    public List<SilArgument> Arguments { get; } = [];
    public List<SilInstruction> Instructions { get; } = [];

    public SilInstruction? Terminator =>
        Instructions.Count > 0 && Instructions[^1].IsTerminator ? Instructions[^1] : null;

    public IEnumerable<SilBlock> Successors =>
        Terminator switch {
            BranchInst branch => [branch.Target],
            CondBranchInst conditional => [conditional.WhenTrue, conditional.WhenFalse],
            SwitchEnumInst switchEnum => [..switchEnum.Cases.Select(static c => c.Target), ..(switchEnum.Default is { } d ? [d] : Array.Empty<SilBlock>())],
            TryApplyInst tryApply => [tryApply.Normal, tryApply.Error],
            _ => [],
        };

    public SilArgument AddArgument(SilType type, bool guaranteed = false) {
        var argument = new SilArgument(this, type, guaranteed);
        Arguments.Add(argument);
        return argument;
    }

    public T Append<T>(T instruction) where T : SilInstruction {
        instruction.Block = this;
        Instructions.Add(instruction);
        return instruction;
    }
}

/// <summary>A SIL function: a signature and blocks, the first of which is the entry.</summary>
public sealed class SilFunction(string name, FunctionType type) {
    public string Name { get; } = name;

    /// <summary>The module the function belongs to, once it is added to one.</summary>
    public SilModule? Module { get; internal set; }

    /// <summary>
    ///     For a function that returns a reference, the parameters the result
    ///     may refer into — its summary, inferred by root inference and part
    ///     of its interface (docs/02 §2.4). Printed as <c>roots(0, 1)</c>.
    /// </summary>
    public IReadOnlyList<int>? ResultRoots { get; set; }
    public FunctionType Type { get; } = type;
    public List<SilBlock> Blocks { get; } = [];

    /// <summary>The source file and line it was declared at, for debug information.</summary>
    public string? SourceFile { get; set; }

    public int Line { get; set; }

    /// <summary>
    ///     For a thunk that only forwards to another function — an
    ///     existential's witness — that function's name: a debugger steps
    ///     through the thunk into it, rather than over the call.
    /// </summary>
    public string? TrampolineTarget { get; set; }

    /// <summary>The name of each parameter, <c>self</c> included, for a debugger.</summary>
    public List<string> ParameterNames { get; } = [];

    /// <summary>
    ///     The blocks within the function's body that a debugger tells apart,
    ///     so that a name shadowed in one shows only there; scope <c>n</c> of
    ///     a <see cref="SilLocation" /> is the <c>n</c>-th.
    /// </summary>
    public List<SilScope> Scopes { get; } = [];

    public SilBlock Entry => Blocks[0];

    public SilBlock AddBlock() {
        var block = new SilBlock(this);
        Blocks.Add(block);
        return block;
    }

    /// <summary>Drops blocks control cannot reach from the entry.</summary>
    public void RemoveUnreachableBlocks() {
        if (Blocks.Count == 0) {
            return;
        }

        var seen = new HashSet<SilBlock> { Entry };
        var pending = new Stack<SilBlock>();
        pending.Push(Entry);
        while (pending.Count > 0) {
            foreach (var next in pending.Pop().Successors) {
                if (seen.Add(next)) {
                    pending.Push(next);
                }
            }
        }

        Blocks.RemoveAll(block => !seen.Contains(block));
    }
}

public sealed class SilModule {
    readonly Dictionary<string, SilFunction> _byName = new(StringComparer.Ordinal);

    public List<SilFunction> Functions { get; } = [];

    /// <summary>
    ///     Whether the dynamic exclusivity markers are kept (docs/02 §2.3): on
    ///     in debug builds, off in release unless <c>--exclusivity=checked</c>.
    /// </summary>
    public bool CheckExclusivity { get; set; } = true;

    /// <summary>The function a program starts in: the one marked <c>@main</c>, or else <c>main</c>.</summary>
    public string? EntryPoint { get; set; }

    /// <summary>Set when the module is one unit of a program compiled separately (<see cref="SilUnit" />).</summary>
    public SilUnitInfo? Unit { get; set; }

    readonly Dictionary<string, NominalType> _types = new(StringComparer.Ordinal);

    /// <summary>The structs and enums the module declares, in declaration order.</summary>
    public List<NominalType> Types { get; } = [];

    /// <summary>
    ///     The globals, by name, with their types, in the order they are
    ///     initialised: <c>sil_global @M.total : $Int</c>.
    /// </summary>
    public List<(string Name, SilType Type)> Globals { get; } = [];

    /// <summary>
    ///     The C functions the module calls (<c>@extern</c>, SYNTAX.md §24),
    ///     by the name the module calls each by, with its C symbol and its
    ///     signature: <c>sil_extern @M.c_abs : $(I32) -> I32 = "abs"</c>. A
    ///     call is an ordinary <c>apply</c>; the code generator makes it with
    ///     C's calling convention.
    /// </summary>
    public SortedDictionary<string, (string Symbol, FunctionType Type)> Foreign { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     The module's functions C calls (<c>@export</c>), by name, with the
    ///     C symbol each is called by: <c>sil_export @M.entry = "sable_entry"</c>.
    ///     They are kept however little the program itself calls them.
    /// </summary>
    public SortedDictionary<string, string> Exports { get; } = new(StringComparer.Ordinal);

    public NominalType? FindType(string name) => _types.GetValueOrDefault(name);

    public T AddType<T>(T type) where T : NominalType {
        Types.Add(type);
        _types[type.Name] = type;
        return type;
    }

    public void RemoveType(NominalType type) {
        Types.Remove(type);
        _types.Remove(type.Name);
    }

    public SilFunction? Find(string name) => _byName.GetValueOrDefault(name);

    public SilFunction Add(SilFunction function) {
        Functions.Add(function);
        _byName[function.Name] = function;
        function.Module = this;
        return function;
    }

    Dictionary<SilType, bool>? _deinits;

    /// <summary>
    ///     True when destroying a value of the type may run a <c>deinit</c> —
    ///     code of the program's own, which could write any storage. A class
    ///     may if it, a subclass, or anything its fields hold has one; a
    ///     closure, an existential or an <c>Object</c> may hold anything.
    /// </summary>
    public bool MayRunDeinit(SilType type) {
        _deinits ??= [];
        if (_deinits.TryGetValue(type, out var known)) {
            return known;
        }

        // Assumed false while it is worked out, so that a type holding itself ends.
        _deinits[type] = false;
        var result = type switch {
            _ when type.IsTrivial => false,
            _ when type == SilType.String => false,
            _ when type == SilType.Object => true,
            ThickFunctionType => true,
            ClassType classType => Types.OfType<ClassType>().Where(c => c.IsSubclassOf(classType))
                .Any(c => c.Deinit is not null || c.Fields.Any(f => MayRunDeinit(f.Type))),
            StructType structType => structType.Deinit is not null || structType.Fields.Any(f => MayRunDeinit(f.Type)),
            TupleType tuple => tuple.Elements.Any(MayRunDeinit),
            EnumType enumType => enumType.Cases.Any(c => c.Payload is { } payload && MayRunDeinit(payload)),
            ArrayType array => MayRunDeinit(array.Element),

            // Releasing a weak or unowned reference may free memory, but runs no deinit.
            WeakStorageType or UnownedStorageType => false,
            _ => true,
        };

        _deinits[type] = result;
        return result;
    }

    public override string ToString() => SilPrinter.Print(this);
}

// ===========================================================================
// Builtins
// ===========================================================================

/// <summary>
///     The operations <c>builtin</c> may name: arithmetic, comparison and
///     logic on the scalar types, the string and character operations the
///     runtime provides, and the array operations. Integer arithmetic traps on
///     overflow, as the language's safety promises; floating-point does not.
/// </summary>
/// <remarks>
///     A builtin borrows its operands, except those its signature says it
///     consumes — the element <c>array_append</c> stores — and gives an owned
///     result. The array operations work for any element type, so their
///     signatures are worked out from the operands.
/// </remarks>
public static class SilBuiltins {
    public sealed record Signature(ImmutableArray<SilType> Operands, SilType Result) {
        /// <summary>The operands the builtin takes ownership of.</summary>
        public ImmutableArray<int> Consumed { get; init; } = [];
    }

    static readonly Dictionary<string, Signature> Table = Build();

    /// <summary>The names whose signature depends on the element type of the array they work on.</summary>
    public static readonly ImmutableArray<string> ArrayOperations =
        ["array_empty", "array_count", "array_get", "array_get_borrowed", "array_append", "array_set_unique", "array_element_addr", "array_make_unique", "array_set", "array_remove_last", "array_clear", "array_slice", "region_alloc"];

    /// <summary>A builtin with fixed operand types, by name.</summary>
    public static Signature? Find(string name) => Table.GetValueOrDefault(name);

    /// <summary>
    ///     The signature of a builtin applied to operands of these types, or
    ///     null when it does not apply. <c>array_empty</c> takes nothing, so
    ///     its element type comes from the result it is declared to give.
    /// </summary>
    public static Signature? Resolve(string name, IReadOnlyList<SilType> operands, SilType? result = null) {
        if (Table.TryGetValue(name, out var fixedSignature)) {
            return fixedSignature;
        }

        if (name == "array_empty") {
            return operands.Count == 0 && result is ArrayType ? new([], result) : null;
        }

        if (name.StartsWith("inline_", StringComparison.Ordinal)) {
            return InlineSignature(name, operands, result);
        }

        // Tasks: spawned, given a body and the task object (which the runtime then holds), waited for, yielded to.
        switch (name) {
            case "task_spawn" when operands is [FunctionType, ClassType]:
                return new([operands[0], operands[1]], SilType.Int) { Consumed = [1] };
            case "task_wait" or "task_release" when operands is [var handle] && handle == SilType.Int:
                return new([SilType.Int], SilType.Unit);
            // Off the run queue for at least this many nanoseconds, while the other tasks run.
            case "task_sleep" when operands is [var nanoseconds] && nanoseconds == SilType.Int:
                return new([SilType.Int], SilType.Unit);
            case "task_yield" or "task_drain" when operands.Count == 0:
                return new([], SilType.Unit);
        }

        // Strings joined end to end in one allocation: an interpolation, or 'a + b + c'.
        if (name == "string_concat_many") {
            return operands.Count >= 2 && operands.All(static o => o == SilType.String) ? new([..operands], SilType.String) : null;
        }

        // 'a === b': whether two references are to the one object.
        if (name == "ref_identical") {
            return operands is [ClassType, ClassType] ? new([operands[0], operands[1]], SilType.Bool) : null;
        }

        // 'x is Dog': whether the object is of the class named, or a subclass of it.
        if (name.StartsWith("class_is:", StringComparison.Ordinal)) {
            return operands is [ClassType] ? new([operands[0]], SilType.Bool) : null;
        }

        // The dynamic exclusivity markers (docs/02 §2.3): an access to storage
        // begins, read or modify, and ends — a conflict with one in progress traps.
        if (name is "access_begin_read" or "access_begin_modify" or "access_end") {
            return operands is [AddressType address] ? new([address], SilType.Unit) : null;
        }

        // Room for a value in a region: its address, which the caller initialises.
        if (name == "region_alloc") {
            return operands is [var region] && region == SilType.Region && result is AddressType ? new([SilType.Region], result) : null;
        }

        SilType? Element(int index, bool address) {
            if (index >= operands.Count) {
                return null;
            }

            var type = address ? (operands[index] as AddressType)?.Pointee : operands[index];
            return (type as ArrayType)?.Element;
        }

        switch (name) {
            case "array_count" when Element(0, false) is not null:
                return new([operands[0]], SilType.Int);

            // A borrowed read leaves the element where it is: no copy, for as
            // long as the array it is in is alive and unchanged.
            case "array_get" or "array_get_borrowed" when Element(0, false) is { } element:
                return new([operands[0], SilType.Int], element);

            case "array_append" when Element(0, true) is { } element:
                return new([operands[0], element], SilType.Unit) { Consumed = [1] };

            // In place, on an array already made the only reference to its buffer.
            case "array_set" or "array_set_unique" when Element(0, true) is { } element:
                return new([operands[0], SilType.Int, element], SilType.Unit) { Consumed = [2] };

            case "array_make_unique" when Element(0, true) is not null:
                return new([operands[0]], SilType.Unit);

            // Where an element is, to pass inout ('bump(&values[0])'): the
            // array made the only reference to its buffer, the index checked.
            // Good until the array next changes, which the access to it, for
            // the length of the call, rules out.
            case "array_element_addr" when Element(0, true) is { } element:
                return new([operands[0], SilType.Int], SilType.Address(element));

            case "array_remove_last" when Element(0, true) is { } element:
                return new([operands[0]], element);

            case "array_clear" when Element(0, true) is not null:
                return new([operands[0]], SilType.Unit);

            // A copy of elements [from, to): an array of its own.
            case "array_slice" when Element(0, false) is not null:
                return new([operands[0], SilType.Int, SilType.Int], operands[0]);

            default:
                return null;
        }
    }

    public static IEnumerable<string> Names =>
        Table.Keys.Concat(ArrayOperations).Concat(["access_begin_read", "access_begin_modify", "access_end", "task_spawn", "task_wait", "task_release", "task_yield", "task_sleep", "task_drain"]);

    static SilType? Pointee(SilType? type) => (type as InlinePointerType)?.Pointee;

    static bool IsInline(SilType? type) => type is StructType { IsInline: true };

    /// <summary>The element of an inline array, as a pointer to one: a struct's view, or a scalar's place.</summary>
    static SilType? ElementPointer(SilType? array) =>
        Pointee(array) is InlineArrayType inlineArray ? new InlinePointerType(inlineArray.Element) : null;

    /// <summary>
    ///     The inline-layout operations (docs/02 §2.5). Blocks are counted and
    ///     copied on write; everything inside one is reached through a
    ///     pointer — <c>inline_base</c> to read, <c>inline_unique</c> to write —
    ///     and those that change its size take the address it is held at.
    /// </summary>
    static Signature? InlineSignature(string name, IReadOnlyList<SilType> operands, SilType? result) {
        SilType? Operand(int i) => i < operands.Count ? operands[i] : null;
        var rootSlot = (Operand(0) as AddressType)?.Pointee;
        var bytes = new ArrayType(SilType.U8);

        var field = name.StartsWith("inline_field.", StringComparison.Ordinal)
            && int.TryParse(name["inline_field.".Length..], out var index)
            ? index
            : -1;
        if (field >= 0) {
            return Pointee(Operand(0)) is StructType { IsInline: true } owner && field < owner.Fields.Count
                ? new([operands[0]], new InlinePointerType(owner.Fields[field].Type))
                : null;
        }

        switch (name) {
            case "inline_new" or "inline_new_stack" when operands.Count == 0 && IsInline(result):
                return new([], result!);
            case "inline_base" when IsInline(Operand(0)):
                return new([operands[0]], new InlinePointerType(operands[0]));
            case "inline_unique" when IsInline(rootSlot):
                return new([operands[0]], new InlinePointerType(rootSlot!));
            case "inline_copy" when IsInline(Pointee(Operand(0))):
                return new([operands[0]], Pointee(operands[0])!);
            case "inline_load" when InlineLayout.Scalar(Pointee(Operand(0)) ?? SilType.Unit) is not null:
                return new([operands[0]], Pointee(operands[0])!);
            case "inline_store" when operands.Count == 2 && Pointee(Operand(0)) == Operand(1) && InlineLayout.Scalar(operands[1]) is not null:
                return new([operands[0], operands[1]], SilType.Unit);
            case "inline_count" when Pointee(Operand(0)) is InlineArrayType:
                return new([operands[0]], SilType.Int);
            case "inline_first" when ElementPointer(Operand(0)) is { } first:
                return new([operands[0]], first);
            case "inline_next" when ElementPointer(Operand(0)) is { } element && Operand(1) == element:
                return new([operands[0], element], element);
            case "inline_at" when ElementPointer(Operand(0)) is { } element && Operand(1) == SilType.Int:
                return new([operands[0], SilType.Int], element);
            case "inline_append" when IsInline(rootSlot) && Pointee(Operand(1)) is InlineArrayType array
                && (Operand(2) == array.Element || Operand(2) == new InlinePointerType(array.Element)):
                return new([operands[0], operands[1], operands[2]], SilType.Unit);
            case "inline_assign_string" when IsInline(rootSlot) && Pointee(Operand(1)) is InlineArrayType { Element: var u8 } && u8 == SilType.U8 && Operand(2) == SilType.String:
                return new([operands[0], operands[1], SilType.String], SilType.Unit);
            case "inline_assign" when IsInline(rootSlot) && Pointee(Operand(1)) is StructType { IsInline: true } nested
                && (Operand(2) == nested || Operand(2) == operands[1]):
                return new([operands[0], operands[1], operands[2]], SilType.Unit);
            case "inline_ref_get" when Pointee(Operand(0)) is RelativeReferenceType reference:
                return new([operands[0]], new InlinePointerType(reference.Target));
            case "inline_is_null" when Operand(0) is InlinePointerType:
                return new([operands[0]], SilType.Bool);
            case "inline_ref_set" when IsInline(rootSlot) && Pointee(Operand(1)) is RelativeReferenceType reference
                && Operand(2) == new InlinePointerType(reference.Target):
                return new([operands[0], operands[1], operands[2]], SilType.Unit);
            case "inline_ref_clear" when Pointee(Operand(0)) is RelativeReferenceType:
                return new([operands[0]], SilType.Unit);
            case "inline_to_string" when Pointee(Operand(0)) is InlineArrayType { Element: var element } && element == SilType.U8:
                return new([operands[0]], SilType.String);
            case "inline_bytes" when IsInline(Pointee(Operand(0))):
                return new([operands[0]], bytes);
            case "inline_from" when Operand(0) == bytes && IsInline(result):
                return new([bytes], result!);
            case "inline_invalid" when IsInline(Operand(0)):
                return new([operands[0]], SilType.String);
            default:
                return null;
        }
    }

    static Dictionary<string, Signature> Build() {
        var table = new Dictionary<string, Signature>(StringComparer.Ordinal);

        void Binary(string op, SilType operand, SilType result) =>
            table[$"{op}_{operand}"] = new([operand, operand], result);

        void Unary(string op, SilType operand) => table[$"{op}_{operand}"] = new([operand], operand);

        void Add(string name, SilType result, params SilType[] operands) => table[name] = new([..operands], result);

        foreach (var integer in SilType.Integers) {
            foreach (var op in new[] { "add", "sub", "mul", "div", "rem", "and", "or", "xor", "shl", "shr", "min", "max" }) {
                Binary(op, integer, integer);
            }

            Unary("neg", integer);
            Unary("not", integer);
            if (SilType.IntegerInfo(integer)!.Value.Signed) {
                Unary("abs", integer);
            }
        }

        foreach (var real in SilType.Floats) {
            // 'rem' is C's fmod (LLVM's frem): exact, with the sign of the dividend.
            foreach (var op in new[] { "add", "sub", "mul", "div", "rem", "min", "max" }) {
                Binary(op, real, real);
            }

            Unary("neg", real);
            Unary("abs", real);
        }

        foreach (var op in new[] { "and", "or", "xor" }) {
            Binary(op, SilType.Bool, SilType.Bool);
        }

        foreach (var type in SilType.Numbers.Concat([SilType.Bool, SilType.Char, SilType.String])) {
            Binary("cmp_eq", type, SilType.Bool);
            Binary("cmp_ne", type, SilType.Bool);
        }

        foreach (var type in SilType.Numbers.Append(SilType.Char)) {
            foreach (var op in new[] { "cmp_lt", "cmp_le", "cmp_gt", "cmp_ge" }) {
                Binary(op, type, SilType.Bool);
            }
        }

        foreach (var printable in SilType.Numbers.Concat([SilType.String, SilType.Bool, SilType.Char])) {
            table[$"print_{printable}"] = new([printable], SilType.Unit);
        }

        // Between any two numeric types: trapping when the value does not fit.
        foreach (var from in SilType.Numbers) {
            foreach (var to in SilType.Numbers) {
                if (from != to) {
                    table[$"convert_{from}_{to}"] = new([from], to);

                    // 'unchecked as': an integer wraps, a float saturates.
                    table[$"wrap_{from}_{to}"] = new([from], to);
                }
            }
        }

        Unary("not", SilType.Bool);

        // Strings. A string is indexed by Unicode scalar: 'count' scalars,
        // 'at' one of them, 'slice' a run of them.
        Add("string_concat", SilType.String, SilType.String, SilType.String);
        Add("string_count", SilType.Int, SilType.String);
        Add("string_at", SilType.Char, SilType.String, SilType.Int);
        Add("string_slice", SilType.String, SilType.String, SilType.Int, SilType.Int);
        Add("string_contains", SilType.Bool, SilType.String, SilType.Char);
        Add("string_index_of", SilType.Int, SilType.String, SilType.Char);

        // Walking a string's scalars from a cursor, which only these read:
        // a byte offset natively, a scalar index in the interpreter.
        Add("string_cursor_end", SilType.Int, SilType.String);
        Add("string_cursor_scalar", SilType.Char, SilType.String, SilType.Int);
        Add("string_cursor_next", SilType.Int, SilType.String, SilType.Int);
        Add("string_trimmed", SilType.String, SilType.String);
        Add("string_split", new ArrayType(SilType.String), SilType.String, SilType.Char);
        Add("string_hash", SilType.Int, SilType.String);
        Add("string_join", SilType.String, new ArrayType(SilType.String), SilType.String);
        Add("string_compare", SilType.Int, SilType.String, SilType.String);
        Add("string_from_Char", SilType.String, SilType.Char);
        foreach (var number in SilType.Numbers) {
            Add($"string_from_{number}", SilType.String, number);
        }

        Add("string_from_Bool", SilType.String, SilType.Bool);

        // Parsing gives whether it worked, and the value when it did.
        Add("string_to_Int", SilType.Tuple([SilType.Bool, SilType.Int]), SilType.String);
        Add("string_to_F64", SilType.Tuple([SilType.Bool, SilType.F64]), SilType.String);

        // The runtime's calls (Sable.IO's floor). Those that can fail give a
        // value either way; 'io_succeeded' then says whether the last one did.
        Add("io_write", SilType.Unit, SilType.String);
        Add("io_write_error", SilType.Unit, SilType.String);
        Add("io_read_line", SilType.String);
        Add("io_read_file", SilType.String, SilType.String);
        Add("io_write_file", SilType.Bool, SilType.String, SilType.String, SilType.Bool);
        Add("io_file_exists", SilType.Bool, SilType.String);
        Add("io_is_directory", SilType.Bool, SilType.String);
        Add("io_remove_file", SilType.Bool, SilType.String);
        Add("io_make_directory", SilType.Bool, SilType.String);
        Add("io_list_directory", new ArrayType(SilType.String), SilType.String);
        Add("io_environment", SilType.String, SilType.String);
        Add("io_argument_count", SilType.Int);
        Add("io_argument", SilType.String, SilType.Int);
        Add("io_succeeded", SilType.Bool);
        Add("time_monotonic", SilType.Int);
        Add("time_wall_clock", SilType.Int);
        Add("time_sleep", SilType.Unit, SilType.Int);
        Add("process_run", SilType.String, SilType.String, new ArrayType(SilType.String), SilType.String);
        Add("process_status", SilType.Int);
        Add("process_error_output", SilType.String);
        Add("process_current_directory", SilType.String);
        Add("process_change_directory", SilType.Bool, SilType.String);
        Add("process_id", SilType.Int);
        Add("process_executable_path", SilType.String);

        Add("region_create", SilType.Region);

        // Emptying a region, all of it or back to a mark: what a 'region frame = pool' block took.
        Add("region_reset", SilType.Unit, SilType.Region);
        Add("region_push", SilType.Unit, SilType.Region);
        Add("region_pop", SilType.Unit, SilType.Region);
        Add("char_to_Int", SilType.Int, SilType.Char);
        Add("char_from_Int", SilType.Tuple([SilType.Bool, SilType.Char]), SilType.Int);
        Add("char_is_digit", SilType.Bool, SilType.Char);
        Add("char_is_letter", SilType.Bool, SilType.Char);
        Add("char_is_whitespace", SilType.Bool, SilType.Char);

        return table;
    }
}
