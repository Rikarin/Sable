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

    /// <summary>bfloat16: an F32's 8 exponent bits with 7 of its fraction's, what machine learning stores.</summary>
    public static readonly SilType BF16 = new ScalarType("BF16");

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
    public static readonly ImmutableArray<SilType> Floats = [F64, F32, F16, BF16];

    /// <summary>What an atomic operation works on: every integer up to 64 bits, and Bool.</summary>
    public static readonly ImmutableArray<SilType> AtomicTypes = [Int, I8, I16, I32, I64, U8, U16, U32, U64, UInt, Bool];

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
    public static bool IsFloat(SilType type) => type == F64 || type == F32 || type == F16 || type == BF16;

    /// <summary>A floating-point type's width in bits; null for anything else.</summary>
    public static int? FloatBits(SilType type) =>
        type == F64 ? 64 : type == F32 ? 32 : type == F16 || type == BF16 ? 16 : null;

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

    /// <summary>
    ///     A type's metadata (docs/06 §6.2, <see cref="TypeMetadata" />): a
    ///     pointer to a constant record, never counted. What
    ///     <c>metadata $T</c> gives, and what a <c>TypeMetadata</c> value is.
    /// </summary>
    public static readonly SilType Metadata = new ScalarType("Metadata");

    /// <summary>
    ///     A conformance's witness table (docs/06 §6.2, <see cref="SilWitnessTable" />),
    ///     or null where a type does not conform: a pointer to a constant
    ///     record, never counted. What the conformance registry gives.
    /// </summary>
    public static readonly SilType WitnessTable = new ScalarType("WitnessTable");

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

    /// <summary>
    ///     Whether two types are the same but for which type parameter each
    ///     <see cref="OpaqueType" /> in them names: how a call's arguments
    ///     match a callee's parameters, whose opaque types are its own.
    /// </summary>
    public static bool SameErasingOpaque(SilType a, SilType b) =>
        a == b || ErasedOpaque(a.ToString()) == ErasedOpaque(b.ToString());

    /// <summary>
    ///     With <paramref name="opaqueOnly" />: whether two types differ only
    ///     where one has an opaque type and the other any type — an array of a
    ///     type parameter's values and an array of Ints, laid out alike.
    /// </summary>
    public static bool SameErasingOpaque(SilType a, SilType b, bool opaqueOnly) =>
        (a, b) switch {
            _ when a == b => true,
            (OpaqueType, _) or (_, OpaqueType) => opaqueOnly,
            // A function's convention differs where a value is indirect: only another opaque type's name may.
            (ThickFunctionType, ThickFunctionType) => ErasedOpaque(a.ToString()) == ErasedOpaque(b.ToString()),
            (ArrayType x, ArrayType y) => SameErasingOpaque(x.Element, y.Element, opaqueOnly),
            (SliceType x, SliceType y) => SameErasingOpaque(x.Element, y.Element, opaqueOnly),
            (AddressType x, AddressType y) => SameErasingOpaque(x.Pointee, y.Pointee, opaqueOnly),
            (StructType x, StructType y) => x.Fields.Count == y.Fields.Count && (MentionsOpaque(x) || MentionsOpaque(y)) && GenericBase(x.Name) == GenericBase(y.Name)
                                            && x.Fields.Zip(y.Fields).All(p => p.First.Name == p.Second.Name && SameErasingOpaque(p.First.Type, p.Second.Type, opaqueOnly)),
            (TupleType x, TupleType y) => x.Elements.Length == y.Elements.Length && x.Elements.Zip(y.Elements).All(p => SameErasingOpaque(p.First, p.Second, opaqueOnly)),
            // A generic class's reference, its fields laid out alike whatever its arguments.
            (ClassType x, ClassType y) => x.Fields.Count == y.Fields.Count && (MentionsOpaque(x) || MentionsOpaque(y)) && GenericBase(x.Name) == GenericBase(y.Name)
                                          && x.Fields.Zip(y.Fields).All(p => p.First.Name == p.Second.Name && SameErasingOpaque(p.First.Type, p.Second.Type, opaqueOnly)),
            (EnumType x, EnumType y) => x.Cases.Count == y.Cases.Count && (MentionsOpaque(x) || MentionsOpaque(y))
                                        && x.Cases.Zip(y.Cases).All(p => p.First.Name == p.Second.Name
                                                                         && (p.First.Payload is null ? p.Second.Payload is null : p.Second.Payload is not null && SameErasingOpaque(p.First.Payload, p.Second.Payload, opaqueOnly))),
            _ => false,
        };

    /// <summary>A generic type's name without its type arguments: <c>M.Box</c> of <c>M.Box$of$Int</c>.</summary>
    static string GenericBase(string name) => name.IndexOf("$of$", StringComparison.Ordinal) is >= 0 and var at ? name[..at] : name;

    static string ErasedOpaque(string text) =>
        text.Contains("opaque", StringComparison.Ordinal)
            ? System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(text, @"@opaque\([^)]*\)", "@opaque"), @"\$opaque\$\w*?\$", "$opaque$")
            : text;

    /// <summary>
    ///     True when the type is, or holds, an <see cref="OpaqueType" />: in
    ///     its parts, or in an optional's or a tuple's made in unspecialised
    ///     code, whose names say so (<c>Optional$$opaque$T$</c>).
    /// </summary>
    public static bool MentionsOpaque(SilType type) => type.ToString() is var text && (text.Contains("@opaque(", StringComparison.Ordinal) || text.Contains("$opaque$", StringComparison.Ordinal));

    /// <summary>
    ///     True for a type unspecialised code holds indirectly, as it holds a
    ///     type parameter's value (#297): an <see cref="OpaqueType" />, or an
    ///     optional, an enum or a tuple with one among its parts — not behind a
    ///     reference, as an array's elements are — whose layout its metadata,
    ///     made at run time from its parts', says. A value of one is the
    ///     address of a buffer of that size.
    /// </summary>
    public static bool IsIndirect(SilType type) =>
        type switch {
            OpaqueType => true,
            TupleType tuple => tuple.Elements.Any(IsIndirect),
            StructType structType => MentionsOpaque(structType) && structType.Fields.Any(static f => IsIndirect(f.Type)),
            EnumType enumType => MentionsOpaque(enumType) && enumType.Cases.Any(static c => c.Payload is { } payload && IsIndirect(payload)),
            _ => false,
        };
    public static bool operator !=(SilType? a, SilType? b) => !(a == b);
}

/// <summary>
///     <c>@opaque(T)</c>: a value of a type unspecialised generic code knows
///     only by its metadata (#297, docs/06 §6.2) — the metadata the function
///     it is in is given as its parameter <c>$metadata.T</c>. Its size is
///     known only at run time, so it is held indirectly: natively a value
///     of it is the address of a buffer of the metadata's size in the
///     caller's or the function's frame, and copying, moving and destroying
///     one go through the metadata's value witnesses. A parameter of it is
///     borrowed, the caller's storage; a result of it is written to an
///     address the caller passes (<c>$*@opaque(T)</c>).
/// </summary>
/// <remarks>
///     Its name says only where its metadata is in the function at hand: at
///     a call, a callee's <c>@opaque(T)</c> takes a caller's <c>@opaque(U)</c>,
///     as <see cref="SilType.SameErasingOpaque" /> compares them.
/// </remarks>
public sealed class OpaqueType(string name) : SilType {
    public string Name { get; } = name;
    public override bool IsTrivial => false;
    public override string ToString() => $"@opaque({Name})";
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

    /// <summary>The element of an array or of a slice — what reading one gives — or null for any other type.</summary>
    public static SilType? ElementOf(SilType? type) =>
        type switch {
            ArrayType array => array.Element,
            SliceType slice => slice.Element,
            _ => null,
        };
}

/// <summary>
///     <c>[T:]</c>: a slice (<c>T[:]</c>) — a view of a run of an array's
///     elements, with no copy of them: the array's buffer, where the run
///     starts in it and how many elements it has.
/// </summary>
/// <remarks>
///     The view holds a strong reference to the buffer (docs/02 §2.5, "The
///     array family"): the array copies on its next write while a slice
///     shares it, so the elements a slice sees never change under it, and a
///     slice may be stored or returned like any value. The empty slice is no
///     buffer at all. A write through a slice makes it the only reference to
///     its run first, as an array's does to its buffer.
/// </remarks>
public sealed class SliceType(SilType element) : SilType {
    public SilType Element { get; } = element;
    public override bool IsTrivial => false;
    public override string ToString() => $"[{Element}:]";

    /// <summary>How a slice is laid out: the buffer, the start and the count — as a tuple of them is.</summary>
    public TupleType Parts => new([new ArrayType(Element), Int, Int]);
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
public sealed class FunctionType(ImmutableArray<SilType> parameters, SilType result, SilType? thrown = null, ImmutableArray<bool> owned = default, bool isAsync = false)
    : SilType {
    public ImmutableArray<SilType> Parameters { get; } = parameters;
    public SilType Result { get; } = result;

    /// <summary>
    ///     An <c>async</c> function: one that may suspend. Lowering and the
    ///     passes treat a call to one as any other call; <see cref="AsyncLowering" />,
    ///     last, makes each such function a coroutine — a frame on its task's
    ///     slab and a resume function — and each call to one the start of a
    ///     frame and the polling of it.
    /// </summary>
    public bool IsAsync { get; } = isAsync;

    /// <summary>The same, async or not.</summary>
    public FunctionType WithAsync(bool isAsync) =>
        isAsync == IsAsync ? this : new(Parameters, Result, Thrown, Owned.IsEmpty ? default : Owned, isAsync);

    /// <summary>The error a <c>throws(E)</c> function may throw, or null when it cannot.</summary>
    public SilType? Thrown { get; } = thrown;

    /// <summary>For each parameter, whether it is owned; empty when none is.</summary>
    public ImmutableArray<bool> Owned { get; } = owned.IsDefault || !owned.Contains(true) ? [] : owned;

    public bool IsOwned(int parameter) => !Owned.IsEmpty && Owned[parameter];

    public override string ToString() {
        var parameters = string.Join(", ", Parameters.Select((p, i) => IsOwned(i) ? $"@owned {p}" : p.ToString()));
        var effects = (IsAsync ? " async" : "") + (Thrown is null ? "" : $" throws({Thrown})");
        return $"({parameters}){effects} -> {Result}";
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
        new([..Signature.Parameters, Object], Signature.Result, Signature.Thrown, Signature.Owned.IsEmpty ? default : [..Signature.Owned, false], Signature.IsAsync);

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

    /// <summary>
    ///     A generic type's instance as Sable writes it — <c>M.Box&lt;Int&gt;</c>,
    ///     where <see cref="Name" /> is <c>M.Box$of$Int</c> — for a debugger to
    ///     show; null for any other type, whose name is already the one written.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>A move-only struct or enum: a value of it is never copied, so it has ownership even when its parts are trivial.</summary>
    public bool IsNoncopyable { get; set; }

    /// <summary>
    ///     The protocols the type conforms to by its own declaration or its
    ///     extensions', each by its qualified name, with those they refine: its
    ///     metadata's conformance records (docs/06 §6.2). A class's
    ///     superclass's are its superclass's.
    /// </summary>
    public List<string> Conformances { get; } = [];

    /// <summary>
    ///     A generic type's conformances that hold only where its type
    ///     arguments conform to protocols (<c>extension Box: P where T: P</c>),
    ///     each with what they must conform to, by argument index: what an
    ///     instance made at run time is given records by (#297). An instance
    ///     whose arguments are known has those that hold among <see cref="Conformances" />.
    /// </summary>
    public List<(string Protocol, ImmutableArray<(int Argument, string Protocol)> Requirements)> ConditionalConformances { get; } = [];

    /// <summary>
    ///     A generic type's instance: its type arguments, as its metadata
    ///     lists them — what metadata made at run time will be cached by
    ///     (#297); empty for any other type.
    /// </summary>
    public ImmutableArray<SilType> TypeArguments { get; set; } = [];

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

    /// <summary>
    ///     <c>@packed</c>: its fields one after another, with no padding
    ///     between them, and the struct aligned to a byte (or to
    ///     <see cref="Alignment" />, when that is given too).
    /// </summary>
    public bool IsPacked { get; set; }

    /// <summary>
    ///     <c>@align(n)</c>: the struct is aligned to at least n bytes, and
    ///     its size is a multiple of that; 0 when it is aligned as its fields are.
    /// </summary>
    public int Alignment { get; set; }

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
        var alignment = Math.Max(1, type.Alignment);
        for (var i = 0; i < type.Fields.Count; i++) {
            var (name, fieldType) = type.Fields[i];
            if (FixedSizeOf(fieldType) is { } size) {
                // An '@align(n)' struct, nested, starts at a multiple of n
                // from the start of the value, after zero bytes.
                var fieldAlignment = fieldType is StructType { IsInline: true } nested ? Of(nested).Alignment : 1;
                alignment = Math.Max(alignment, fieldAlignment);
                offset = (offset + fieldAlignment - 1) / fieldAlignment * fieldAlignment;
                fixedSlots.Add(Describe(i, name, fieldType, offset));
                offset += size;
            } else {
                variableSlots.Add(Describe(i, name, fieldType, -1));
            }
        }

        // The fixed fields are rounded up to the alignment, so a value of a
        // fixed size is a multiple of it: a record format's fixed stride.
        Alignment = alignment;
        offset = (offset + alignment - 1) / alignment * alignment;
        Prefix = offset;
        Slots = [..fixedSlots, ..variableSlots];
        FixedSize = variableSlots.Count == 0 ? offset : null;
    }

    public StructType Type { get; }

    /// <summary>The fields in the order they are stored: fixed ones first.</summary>
    public ImmutableArray<Slot> Slots { get; }

    /// <summary>
    ///     What offsets from the start of a value its fixed part is laid out
    ///     at multiples of: its own '@align(n)', or a nested struct's; 1 when
    ///     neither says, and the fields are packed.
    /// </summary>
    public int Alignment { get; }

    /// <summary>The bytes the fixed-size fields take, at the start, padding to the alignment included.</summary>
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
        : type == SilType.F16 || type == SilType.BF16 ? (ScalarKind, 2)
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

    /// <summary>A union, <c>A | B</c> or a declared one, rather than an enum of cases alone: what its metadata's kind says.</summary>
    public bool IsUnion { get; set; }

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

    /// <summary>
    ///     The fields declared <c>let</c>, a superclass's among them: written
    ///     once, by the initialiser, before anything else can read them. Known
    ///     from lowering alone; SIL read as text says none.
    /// </summary>
    public HashSet<int> LetFields { get; } = [];

    /// <summary>
    ///     The first field that may be left empty when the object is made —
    ///     all zeros, holding nothing — or null when every field must be
    ///     stored first: a generator's frame, whose slots fill as its body runs.
    /// </summary>
    public int? EmptyFrom { get; set; }

    /// <summary>
    ///     An async function's frame (<see cref="AsyncLowering" />): made on
    ///     its task's frame slab, not the heap, and given back by
    ///     <c>async_frame_free</c> once its function has finished — never
    ///     counted. Its fields hold what the function keeps while it is
    ///     suspended, as bits: they are never destroyed with it.
    /// </summary>
    public bool IsCoroutineFrame { get; init; }

    /// <summary>
    ///     In an unspecialised async function's frame (#297), the fields that
    ///     each hold the address of storage sized at run time — a slot of a
    ///     type parameter's type, or such a value live across a suspension:
    ///     buffers after the frame's fixed part, on the slab with it, sized by
    ///     the metadata its start function is given, empty when it is made.
    /// </summary>
    public List<int> TailSlots { get; } = [];

    /// <summary>
    ///     A generic class whose objects unspecialised code lays out at run
    ///     time (#297): a field holds a type parameter's value by value, so
    ///     where each field is depends on the metadata of its type arguments.
    ///     Not a closure's context or a frame, whose field of a type parameter's
    ///     type holds the address of a value held elsewhere.
    /// </summary>
    public bool IsLaidOutAtRunTime => IsGenericInstance && Fields.Exists(static f => IsIndirect(f.Type));

    /// <summary>An instance of a generic class the program declares, <c>Test.Slot&lt;Int, String&gt;</c>: not a box, a context or a frame.</summary>
    public bool IsGenericInstance => !TypeArguments.IsEmpty && DisplayName?.Contains('<', StringComparison.Ordinal) == true;

    /// <summary>The methods called through the vtable: each slot's name, and the function in it for this class.</summary>
    public List<(string Method, string Function)> VTable { get; } = [];

    public int MethodIndex(string method) => VTable.FindIndex(m => m.Method == method);

    /// <summary>The function that runs as an object dies, before its fields are destroyed, or null.</summary>
    public string? Deinit { get; set; }

    /// <summary>
    ///     A generic class, its type arguments known only at run time, whose
    ///     declaration has a deinit: what makes its objects there finds it by
    ///     the class's name (<see cref="SilModule.GenericDeinits" />).
    /// </summary>
    public bool HasGenericDeinit { get; set; }

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
///     How unspecialised code finds an associated type's metadata (#297): the
///     <paramref name="Index" />-th associated type of <paramref name="Protocol" />
///     in the witness table of <paramref name="Base" />'s conformance to it —
///     the table the function is given as its parameter
///     <paramref name="Table" />, through the bases <paramref name="Path" />
///     names, or, with no table given, the registry's for the base's metadata.
///     <paramref name="Name" /> and <paramref name="Base" /> are
///     <see cref="OpaqueType" /> names.
/// </summary>
public sealed record SilAssociatedMetadata(string Name, string Base, string Protocol, int Index, string? Table, ImmutableArray<int> Path);

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

    /// <summary>
    ///     Where a trap here happened, as <c>file:line:column</c>, for an
    ///     instruction inlined from another function: its place there, which
    ///     its <see cref="Location" />, the call's, no longer says. Null for
    ///     one in the function it was written in.
    /// </summary>
    public string? InlinedSite { get; set; }

    /// <summary>The place a trap at this instruction reports: where it was written, or null when that is not known.</summary>
    public string? TrapSite(SilFunction function) =>
        InlinedSite ?? (Location is { } at && function.SourceFile is { } file ? $"{file}:{at.Line}:{at.Column}" : null);

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
/// <summary>
///     <c>metadata $T</c>: the type's metadata (docs/06 §6.2) — its kind,
///     layout, value witnesses, fields and conformances, as
///     <see cref="TypeMetadata" /> describes them. Natively a constant record,
///     one per type in the program; in the interpreter, its model.
/// </summary>
public sealed class MetadataInst(SilType described) : SilInstruction {
    public SilType Described { get; } = described;
    public override SilType Type => SilType.Metadata;
    public override string Opcode => "metadata";
}

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

    /// <summary>
    ///     <c>builtin [in_bounds]</c>: an element access whose index a pass has
    ///     proved in range — once, before a loop (<see cref="VersionLoops" />)
    ///     — so that it is not checked again: no compare, no trap, nothing to
    ///     stop LLVM vectorising the loop.
    /// </summary>
    public bool InBounds { get; set; }

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

    /// <summary>
    ///     Stores into an object just made, which nothing else has seen
    ///     (<see cref="InitFields" />): it is not shared with another task, so
    ///     neither need the value be. Known within a build alone; SIL read as
    ///     text does not say so.
    /// </summary>
    public bool IntoFresh { get; set; }

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
    public override SilType Type => ((Address.Type as AddressType)?.Pointee as WeakStorageType)?.Optional ?? SilType.Unit; // the verifier rejects the rest
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
    public override SilType Type => ((Address.Type as AddressType)?.Pointee as UnownedStorageType)?.Referent ?? SilType.Unit; // the verifier rejects the rest
    public override string Opcode => "load_unowned";
}

/// <summary><c>store_unowned %1 to %0</c>: holds an object without keeping it alive, borrowing it.</summary>
public sealed class StoreUnownedInst(SilValue value, SilValue address) : SilInstruction(value, address) {
    public SilValue Value => Operands[0];
    public SilValue Address => Operands[1];
    public override bool HasResult => false;
    public override string Opcode => "store_unowned";
}

/// <summary>
///     <c>weak_copy_value %0</c>: an optional's object held weakly, as a
///     value — what a struct's <c>weak</c> field holds — borrowing the
///     optional. Its own until stored in the struct, or destroyed.
/// </summary>
public sealed class WeakCopyValueInst(SilValue value) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type => Value.Type is EnumType optional ? new WeakStorageType(optional) : Value.Type; // the verifier rejects the rest
    public override string Opcode => "weak_copy_value";
}

/// <summary><c>unowned_copy_value %0</c>: an object held unowned, as a value — what a struct's <c>unowned</c> field holds — borrowing it.</summary>
public sealed class UnownedCopyValueInst(SilValue value) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type => Value.Type is ClassType referent ? new UnownedStorageType(referent) : Value.Type;
    public override string Opcode => "unowned_copy_value";
}

/// <summary><c>strong_copy_weak_value %0</c>: the object a weak value holds, retained, or none if it is gone — <c>load_weak</c> of a value.</summary>
public sealed class StrongCopyWeakValueInst(SilValue value) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type => (Value.Type as WeakStorageType)?.Optional ?? Value.Type;
    public override string Opcode => "strong_copy_weak_value";
}

/// <summary><c>strong_copy_unowned_value %0</c>: the object an unowned value holds, retained; a trap if it is gone.</summary>
public sealed class StrongCopyUnownedValueInst(SilValue value) : SilInstruction(value) {
    public SilValue Value => Operands[0];
    public override SilType Type => (Value.Type as UnownedStorageType)?.Referent ?? Value.Type;
    public override string Opcode => "strong_copy_unowned_value";
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

    /// <summary>
    ///     <c>destroy_value [uncounted]</c>: the only reference to an object
    ///     made in the frame (docs/04 §3.7), which nothing ever copied: it is
    ///     destroyed where it is, its count neither read nor written. One that
    ///     turns out to be on the heap — a block that outgrew its room — is
    ///     released as any is.
    /// </summary>
    public bool Uncounted { get; set; }

    public override bool HasResult => false;
    public override string Opcode => "destroy_value";
}

/// <summary><c>destroy_addr %0</c>: destroys the value in storage, leaving it empty.</summary>
public sealed class DestroyAddrInst(SilValue address) : SilInstruction(address) {
    public SilValue Address => Operands[0];

    /// <summary><c>destroy_addr [uncounted]</c>: the storage holds only uncounted objects; see <see cref="DestroyValueInst.Uncounted" />.</summary>
    public bool Uncounted { get; set; }

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
    FunctionType _type = type;
    public SilValue Reference => Operands[0];
    public string Method { get; } = method;
    public override SilType Type => _type;

    /// <summary>The method seen with another signature: an async one's, as the start of its frame (<see cref="AsyncLowering" />).</summary>
    internal void Retype(FunctionType type) => _type = type;
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
            BuiltinInst { Name: "array_append" or "array_set" or "array_set_unique" or "array_make_unique" or "array_remove_last" or "array_clear" or "array_truncate" or "array_reserve" or "array_rotate" or "array_store_slice" or "array_append_utf8" or "array_append_repeating" or "array_append_holes" or "array_take" or "array_append_all" or "array_element_addr" or "array_element_addr_unique" } array
                => (array.Operands[0], true),
            BuiltinInst { Name: var name } stored when name.StartsWith("bytes_store_array_", StringComparison.Ordinal) => (stored.Operands[0], true),
            _ => null,
        };

    /// <summary>The plain access an instruction makes of shared storage, which a checked build checks; null for none.</summary>
    public static (SilValue Address, bool Modify)? CheckedAccessOf(SilInstruction instruction) =>
        AccessOf(instruction) is { } access && Contains(access.Address) ? access : null;

    /// <summary>
    ///     Storage that nothing changes once it can be read — an object's
    ///     <c>let</c> field, or a part of one — so that no read of it can
    ///     overlap a change.
    /// </summary>
    static bool IsImmutable(SilValue address) =>
        address switch {
            RefElementAddrInst { Reference.Type: ClassType type } field => type.LetFields.Contains(field.Field),
            StructElementAddrInst element => IsImmutable(element.Address),
            TupleElementAddrInst element => IsImmutable(element.Address),
            _ => false,
        };

    /// <summary>
    ///     Puts an <c>access_check_read</c> or <c>access_check_modify</c> before
    ///     every plain access to shared storage, as lowered, but a read of what
    ///     never changes: an object's <c>let</c> field. Explicit, the
    ///     checks are what the passes move and copy: inlined, a callee's
    ///     access through an inout parameter becomes one through the caller's
    ///     field, which is the access in progress and must not be checked
    ///     against itself. A build without the checks drops them, with the
    ///     markers, before its passes run (<see cref="DropChecks" />).
    /// </summary>
    public static void InsertChecks(SilModule module) {
        foreach (var block in module.Functions.SelectMany(static f => f.Blocks)) {
            for (var i = 0; i < block.Instructions.Count; i++) {
                if (CheckedAccessOf(block.Instructions[i]) is not var (address, modify) || !modify && IsImmutable(address)) {
                    continue;
                }

                var check = new BuiltinInst(modify ? "access_check_modify" : "access_check_read", SilType.Unit, address) {
                    Block = block,
                    Location = block.Instructions[i].Location,
                };
                block.Instructions.Insert(i++, check);
            }
        }
    }

    /// <summary>The dynamic exclusivity markers and checks, gone: a build that does not keep them.</summary>
    public static void DropChecks(SilModule module) {
        foreach (var block in module.Functions.SelectMany(static f => f.Blocks)) {
            block.Instructions.RemoveAll(static i => i is BuiltinInst builtin && IsMarker(builtin.Name));
        }
    }

    /// <summary>The builtins of dynamic exclusivity: an access begun and ended, and a plain one checked.</summary>
    public static bool IsMarker(string name) =>
        name is "access_begin_read" or "access_begin_modify" or "access_end" or "access_check_read" or "access_check_modify";
}

public sealed class GlobalAddrInst(string global, SilType type) : SilInstruction {
    public string Global { get; } = global;
    public SilType ValueType { get; } = type;
    public override SilType Type => SilType.Address(ValueType);
    public override string Opcode => "global_addr";
}

/// <summary><c>function_ref @f : $(Int) -&gt; Int</c>: a function as a value.</summary>
public sealed class FunctionRefInst(string function, FunctionType type) : SilInstruction {
    FunctionType _type = type;
    public string Function { get; } = function;
    public override SilType Type => _type;

    /// <summary>The function seen with another signature: its own, once <see cref="AsyncLowering" /> has changed it.</summary>
    internal void Retype(FunctionType type) => _type = type;
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

    /// <summary>
    ///     For branch coverage, the outcome of a decision that entering this
    ///     block means — a block that only that outcome's edges enter — or
    ///     null. Set only when lowering marks decisions (<see cref="SilDecision" />).
    /// </summary>
    public (SilDecision Decision, int Outcome)? Outcome { get; set; }

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

/// <summary>
///     A decision in the source — an <c>if</c>'s condition, a <c>match</c>,
///     the left side of <c>&amp;&amp;</c> — and the outcomes it can have, for
///     branch coverage: lowering, asked to, marks the blocks that stand for
///     each outcome (<see cref="SilBlock.Outcome" />), and coverage counts them.
/// </summary>
/// <param name="At">Where it is written: the keyword or operator that makes it.</param>
/// <param name="Kind">What it is, as written: <c>if</c>, <c>&amp;&amp;</c>, <c>match</c>.</param>
/// <param name="Outcomes">A name for each outcome: <c>true</c> and <c>false</c>, a match's arms.</param>
public sealed record SilDecision(SilLocation At, string Kind, IReadOnlyList<string> Outcomes);

/// <summary>Whether a function is inlined where it is called: as the inliner judges, always, or never.</summary>
public enum SilInline {
    Default,
    Always,
    Never,
}

/// <summary>A SIL function: a signature and blocks, the first of which is the entry.</summary>
public sealed class SilFunction(string name, FunctionType type) {
    public string Name { get; internal set; } = name;

    /// <summary>
    ///     For an instance of a generic function, its name as Sable writes it
    ///     — <c>Demo.describe&lt;Int&gt;</c>, <c>M.Stack&lt;Int&gt;.push</c> —
    ///     where <see cref="Name" /> is <c>Demo.describe$of$Int</c>: for a
    ///     debugger to show. Null for any other function.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>The module the function belongs to, once it is added to one.</summary>
    public SilModule? Module { get; internal set; }

    /// <summary>
    ///     For a function that returns a reference, the parameters the result
    ///     may refer into — its summary, inferred by root inference and part
    ///     of its interface (docs/02 §2.4). Printed as <c>roots(0, 1)</c>.
    /// </summary>
    public IReadOnlyList<int>? ResultRoots { get; set; }

    /// <summary>
    ///     What its source's <c>@inline(always)</c> or <c>@inline(never)</c>
    ///     asks of the inliners, SIL's and LLVM's. Printed as
    ///     <c>inline(always)</c>.
    /// </summary>
    public SilInline Inline { get; set; }

    /// <summary>
    ///     What its source's <c>@vectorised</c> asks: compiled once more for
    ///     the wider vectors a processor may have, and whichever it has called
    ///     (LlvmEmitter.Vectorised.cs). Not inlined in SIL, as its callers are
    ///     not. Printed as <c>vectorised</c>.
    /// </summary>
    public bool Vectorised { get; set; }

    public FunctionType Type { get; internal set; } = type;
    public List<SilBlock> Blocks { get; } = [];

    /// <summary>The decisions of its source that lowering marked, for branch coverage: each kept though its blocks may be gone, as code never reached.</summary>
    public List<SilDecision> Decisions { get; } = [];

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
    ///     True for code that takes a type parameter's metadata
    ///     (<c>$metadata.T</c>, #297): its <see cref="OpaqueType" />s are sized
    ///     and copied by it, so its body means nothing inlined into another.
    /// </summary>
    public bool IsUnspecialised => ParameterNames.Exists(static n => n.StartsWith("$metadata.", StringComparison.Ordinal));

    /// <summary>
    ///     In unspecialised code, the metadata of each associated type its
    ///     opaque values have (<c>@opaque(S.Element)</c>), by how it is found
    ///     from the metadata and tables the function is given. Printed as
    ///     <c>associated("S.Element", "S", "Sable.Core.Sequence", 0, "$witnesses.S.Collection", 0)</c>.
    /// </summary>
    public List<SilAssociatedMetadata> AssociatedMetadata { get; } = [];

    /// <summary>
    ///     For an unspecialised async function's resume and drop functions
    ///     (#297), given only its frame: the frame, whose fields of the
    ///     parameters' names hold the metadata and tables the function was
    ///     started with (<c>$metadata.T</c>), which its opaque values are sized
    ///     and copied by.
    /// </summary>
    public ClassType? HiddenFrame { get; set; }

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

    /// <summary>
    ///     Whether compiled code's retains and releases check the object they
    ///     count (a profile's <c>arc-checks</c>): one whose deinit has run, its
    ///     count zero, is a reference that outlived its object, and traps
    ///     rather than counting freed memory. Off unless a profile asks.
    /// </summary>
    public bool CheckArc { get; set; }

    /// <summary>The function a program starts in: the one marked <c>@main</c>, or else <c>main</c>.</summary>
    public string? EntryPoint { get; set; }

    /// <summary>
    ///     A library's module: it has no entry point, and what C calls — its
    ///     exports — and its globals' initialiser and deinitialiser are where
    ///     it is entered (docs/05 §5.2).
    /// </summary>
    public bool IsLibrary { get; set; }

    /// <summary>
    ///     Built with <c>--deny-reflection</c> (docs/06 §6.1): the types'
    ///     field descriptors carry no names, and <c>Mirror</c> and
    ///     <c>TypeInfo</c> were refused (S3450).
    /// </summary>
    public bool DenyReflection { get; set; }

    /// <summary>
    ///     Lowered with generic code compiled once (<see cref="GenericsMode.Unspecialised" />):
    ///     the program's generic classes it makes metadata for are registered
    ///     as it starts, so that metadata made at run time for the same type
    ///     arguments is the same record (#297).
    /// </summary>
    public bool UnspecialisedGenerics { get; set; }

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
    ///     The C functions of <see cref="Foreign" /> that are variadic, with
    ///     how many of their parameters are fixed: a call to C's '...' is
    ///     declared once for the types it passes after them,
    ///     <c>sil_extern @M.f$va$Int : $(I32, Int) -> F64 = "f" variadic 1</c>.
    /// </summary>
    public SortedDictionary<string, int> ForeignVariadic { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     The module's functions C calls (<c>@export</c>), by name, with the
    ///     C symbol each is called by: <c>sil_export @M.entry = "sable_entry"</c>.
    ///     They are kept however little the program itself calls them. A
    ///     global may be here too (<c>@export var</c>): its storage is then
    ///     named by the C symbol.
    /// </summary>
    public SortedDictionary<string, string> Exports { get; } = new(StringComparer.Ordinal);

    public NominalType? FindType(string name) => _types.GetValueOrDefault(name);

    /// <summary>
    ///     Whether every type's metadata carries field descriptors, each with
    ///     its field's type metadata (docs/06 §6.2): on when the program reads
    ///     them (<c>TypeMetadata.fieldCount</c> and the like) — and, once
    ///     generic code is compiled unspecialised (#297), where that code
    ///     finds a generic type's field offsets. Off, only a class's metadata
    ///     has them, without the field types: what the leak report names.
    /// </summary>
    public bool FieldDescriptors { get; set; }

    readonly Dictionary<(string Type, string Protocol), SilWitnessTable> _witnessTables = [];

    /// <summary>
    ///     The witness tables of the program's conformances (docs/06 §6.2), in
    ///     the order they were made: <c>sil_witness_table $T : "M.P" { … }</c>.
    ///     Each is in its conformer's metadata, in the conformance record the
    ///     registry finds it by.
    /// </summary>
    public IReadOnlyList<SilWitnessTable> WitnessTables => _witnessTableList;

    readonly List<SilWitnessTable> _witnessTableList = [];

    /// <summary>The witness table of a type's conformance to a protocol, or null when none was made.</summary>
    public SilWitnessTable? FindWitnessTable(SilType conformer, string protocol) =>
        _witnessTables.GetValueOrDefault((conformer.ToString(), protocol));

    public SilWitnessTable AddWitnessTable(SilWitnessTable table) {
        if (_witnessTables.TryAdd((table.Conformer.ToString(), table.Protocol), table)) {
            _witnessTableList.Add(table);
        }

        return _witnessTables[(table.Conformer.ToString(), table.Protocol)];
    }

    /// <summary>What each built-in type (<c>Int</c>, <c>String</c>, …) declares it conforms to, by the type's name: its conformance records.</summary>
    public Dictionary<string, List<string>> PrimitiveConformances { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     For each type unspecialised code was given a box of, by the type's
    ///     name: the function that writes such a value out as an interpolation
    ///     would, given its box (<c>generic_describe</c>, #297). Natively, each
    ///     is registered with the runtime, with the type's metadata, as the
    ///     program starts.
    /// </summary>
    public Dictionary<string, (SilType Type, string Function)> Describers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     The same for <c>"\(x:?)"</c>: the function that writes such a value
    ///     out as <c>Debug</c> describes it (<c>generic_debug_describe</c>).
    /// </summary>
    public Dictionary<string, (SilType Type, string Function)> DebugDescribers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     For each error type unspecialised code may throw on as
    ///     <c>any Error</c>, by the type's name: the function that boxes such
    ///     a value as one, given where it is and its metadata
    ///     (<c>opaque_erase_error</c>, #297) — made where the type is known,
    ///     and registered with the runtime as describers are.
    /// </summary>
    public Dictionary<string, (SilType Type, string Function)> ErrorErasers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     For each type a call that opens a Type may erase a result of, by
    ///     the type's name: the function that copies such a value into the
    ///     box an existential keeps one of its type in, given where it is and
    ///     its metadata (<c>opaque_box</c>, #297 stage 6) — made where the
    ///     type is known, and registered with the runtime as describers are.
    /// </summary>
    public Dictionary<string, (SilType Type, string Function)> Boxers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     For each generic class with a deinit whose initialiser is compiled
    ///     once, by its name: the function that runs the deinit of an object
    ///     whose class the runtime made, registered with it
    ///     (<c>sable_register_generic_deinit</c>, #297).
    /// </summary>
    public Dictionary<string, string> GenericDeinits { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     A type's conformance records, by protocol name: one per protocol
    ///     the type declares it conforms to (a class's own; its superclass's
    ///     are its superclass's), each with the conformance's witness table
    ///     where this module made one. Which records a type has depends on its
    ///     declarations alone, so every unit of a program writes the same.
    /// </summary>
    public List<(string Protocol, SilWitnessTable? Witnesses)> ConformancesOf(SilType type) {
        var declared = type is NominalType nominal ? nominal.Conformances : PrimitiveConformances.GetValueOrDefault(type.ToString()) ?? [];
        return [..declared.Distinct(StringComparer.Ordinal).Select(p => (p, FindWitnessTable(type, p)))];
    }

    /// <summary>Whether the type, or a superclass, has a record of its conformance to the protocol.</summary>
    public bool HasConformance(SilType type, string protocol) {
        for (var at = type; at is not null; at = (at as ClassType)?.Superclass) {
            if (ConformancesOf(at).Exists(c => c.Protocol == protocol)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Gives an instance of a generic type whose type arguments are known
    ///     the records of the conditional conformances it has from its
    ///     declaration (<see cref="NominalType.ConditionalConformances" />)
    ///     whose requirements its arguments meet.
    /// </summary>
    public void MeetConditionalConformances(NominalType instance) {
        var arguments = instance.TypeArguments;
        if (arguments.IsDefaultOrEmpty || arguments.Any(SilType.MentionsOpaque)) {
            return;
        }

        foreach (var (protocol, requirements) in instance.ConditionalConformances) {
            if (!instance.Conformances.Contains(protocol)
                && requirements.All(r => r.Argument < arguments.Length && HasConformance(arguments[r.Argument], r.Protocol))) {
                instance.Conformances.Add(protocol);
            }
        }
    }

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

    /// <summary>Gives a function of the module another name, which it is then found by.</summary>
    internal void Rename(SilFunction function, string name) {
        _byName.Remove(function.Name);
        function.Name = name;
        _byName[name] = function;
    }

    /// <summary>
    ///     Set once <see cref="AsyncLowering" /> has made the async functions
    ///     coroutines: what runs the module — the interpreter, the code
    ///     generator — needs it done, and done once. The verifier no longer
    ///     holds the module to ownership after it.
    /// </summary>
    public bool AsyncLowered { get; set; }

    /// <summary>Removes the functions that match, from the list and from <see cref="Find" /> both.</summary>
    public int RemoveAll(Predicate<SilFunction> match) {
        foreach (var function in Functions) {
            if (match(function) && _byName.TryGetValue(function.Name, out var named) && ReferenceEquals(named, function)) {
                _byName.Remove(function.Name);
            }
        }

        return Functions.RemoveAll(match);
    }

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
            SliceType slice => MayRunDeinit(slice.Element),

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
    /// <summary>
    ///     An <c>array_literal</c> of numbers, Bools or Chars, every one written
    ///     as a literal: natively a constant in read-only memory, made once
    ///     and never counted (#265).
    /// </summary>
    public static bool IsConstantArray(BuiltinInst literal) =>
        literal is { Name: "array_literal", Type: ArrayType array }
        && (array.Element == SilType.Int || array.Element == SilType.Bool || array.Element == SilType.Char || array.Element == SilType.F64
            || array.Element == SilType.F32 || SilType.IntegerInfo(array.Element) is not null)
        && literal.Operands.All(static o => o is IntegerLiteralInst or FloatLiteralInst);

    public sealed record Signature(ImmutableArray<SilType> Operands, SilType Result) {
        /// <summary>The operands the builtin takes ownership of.</summary>
        public ImmutableArray<int> Consumed { get; init; } = [];
    }

    static readonly Dictionary<string, Signature> Table = Build();

    /// <summary>
    ///     The names whose signature depends on the element type of the array
    ///     they work on. Those that read elements, or write one in place, work
    ///     on a slice (<c>[T:]</c>) as on an array; those that change the
    ///     count work on an array alone.
    /// </summary>
    public static readonly ImmutableArray<string> ArrayOperations =
        ["array_empty", "array_count", "array_get", "array_get_borrowed", "array_append", "array_set_unique", "array_element_addr", "array_element_addr_unique", "array_make_unique", "array_set", "array_remove_last", "array_clear", "array_truncate", "array_slice", "slice_of", "slice_of_borrowed", "array_of_slice", "slice_of_take", "array_of_slice_take", "array_capacity", "array_reserve", "array_rotate", "array_store_slice", "array_append_utf8", "array_append_repeating", "array_append_holes", "array_take", "array_append_all", "array_mismatch", "region_alloc"];

    /// <summary>
    ///     The prefix of the wrapping forms of integer arithmetic
    ///     (<c>wrapping_add_Int</c>), which never trap: see <see cref="WrappingArithmetic" />.
    /// </summary>
    public const string WrappingPrefix = "wrapping_";

    /// <summary>Whether two values of the type are equal exactly when their bits are: no float is (NaN, and zero's two signs).</summary>
    public static bool ComparesByBits(SilType type) =>
        SilType.IntegerInfo(type) is not null || type == SilType.Bool || type == SilType.Char;

    /// <summary>What <c>blackBox(value)</c> lowers to: an address the optimiser knows nothing of the use of (SilGen's <c>BlackBox</c>).</summary>
    public const string BlackBox = "black_box";

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

        if (SimdBuiltins.Is(name)) {
            return SimdBuiltins.Resolve(name, operands, result);
        }

        if (name == "array_empty") {
            return operands.Count == 0 && result is ArrayType or SliceType ? new([], result) : null;
        }

        // '[0, 7, 14]': an array of its elements, each a literal of the
        // element type — natively a constant, never counted, which copy on
        // write copies before it is changed (#265).
        if (name is "array_literal" or "array_literal_stack") {
            return result is ArrayType literal && operands.Count > 0 && operands.All(o => o == literal.Element)
                ? new([..operands], result) { Consumed = [..Enumerable.Range(0, operands.Count)] }
                : null;
        }

        if (name.StartsWith("inline_", StringComparison.Ordinal)) {
            return InlineSignature(name, operands, result);
        }

        // Tasks: spawned, given a body and the task object (which the runtime
        // then holds), a priority (-1: inherited) and whether detached;
        // waited for, yielded to, given a cancellation handler and its context.
        switch (name) {
            case "task_spawn" when operands is [FunctionType, ClassType, var priority, var detached] && priority == SilType.Int && detached == SilType.Bool:
                return new([..operands], SilType.Int) { Consumed = [1] };
            case "task_handler_push" when operands is [FunctionType, ClassType]:
                return new([..operands], SilType.Unit) { Consumed = [1] };
            // Work run on the blocking pool while the task waits: a function and the object it is given, borrowed.
            case "task_blocking" when operands is [FunctionType, ClassType]:
                return new([..operands], SilType.Unit);
            case "task_wait" or "task_release" or "task_cancel" when operands is [var handle] && handle == SilType.Int:
                return new([SilType.Int], SilType.Unit);
            // Off the run queue for at least this many nanoseconds, while the other tasks run.
            case "task_sleep" when operands is [var nanoseconds] && nanoseconds == SilType.Int:
                return new([SilType.Int], SilType.Unit);
            case "task_yield" or "task_drain" when operands.Count == 0:
                return new([], SilType.Unit);
            // The context a closure made with none is called with: what a
            // call of its code made directly passes (Simplify's DirectCalls).
            case "no_context" when operands.Count == 0:
                return new([], SilType.Object);
            // A block ran, for 'sable test --coverage' (Coverage.cs): its counter's number.
            case Coverage.Builtin when operands is [var counter] && counter == SilType.Int:
                return new([SilType.Int], SilType.Unit);
        }

        // Strings joined end to end in one allocation: an interpolation, or 'a + b + c'.
        if (name == "string_concat_many") {
            return operands.Count >= 2 && operands.All(static o => o == SilType.String) ? new([..operands], SilType.String) : null;
        }

        // Whether the object the address holds is referred to from there
        // alone, and may be changed in place: an existential's box, before
        // a write through it (copy on write).
        if (MetadataSignature(name, operands, result) is { } metadata) {
            return metadata;
        }

        if (name == "ref_is_unique") {
            // Or of a struct that holds the object and nothing else: a
            // '@builtin' of the library's, on a value that copies on write.
            return operands is [AddressType { Pointee: ClassType or StructType { Fields: [(_, ClassType)] } }] ? new([operands[0]], SilType.Bool) : null;
        }

        // A value crossing from one task to another — a task's result, an
        // actor call's argument or result: every object it holds shared,
        // counted atomically from then on (runtime/tasks.c's sable_share).
        if (name == "share") {
            return operands is [var shared] ? new([shared], SilType.Unit) : null;
        }

        // A value about to be put in an object, or an array's or a box's in
        // the slot at an address, or what the storage inside it holds now:
        // shared if the object is. And into a global: if threads were started
        // (ShareStores). What a box holds is of the type its metadata says.
        if (name == "share_into_at" && operands is [AddressType boxed, AddressType held, var described] && described == SilType.Metadata) {
            return new([boxed, held, described], SilType.Unit);
        }

        if (name is "share_into" or "share_into_at") {
            return operands is [ClassType or AddressType, var into] && (name == "share_into" || into is AddressType) ? new([operands[0], into], SilType.Unit) : null;
        }

        if (name is "share_global" or "share_global_at") {
            return operands is [var global] && (name == "share_global" || global is AddressType) ? new([global], SilType.Unit) : null;
        }

        // An object whose initialiser threw, on its way out: it was never
        // made, so no deinit of its runs when it goes, though the fields
        // set so far are destroyed as ever.
        if (name == "object_unmade") {
            return operands is [ClassType] ? new([operands[0]], SilType.Unit) : null;
        }

        // A value at a raw address: copied out (raw_load, its alignment the
        // type's; raw_load_unaligned, any), moved out, leaving the memory
        // uninitialised (raw_take), or moved in, to memory that holds none
        // (raw_init). What type is the declaration's: the result, or the
        // value given.
        if (name is "raw_load" or "raw_load_unaligned" or "raw_take") {
            return operands is [var address] && address == SilType.Int && result is not null && result != SilType.Unit
                ? new([SilType.Int], result)
                : null;
        }

        if (name is "raw_init" or "raw_init_unaligned") {
            return operands is [var address, var value] && address == SilType.Int ? new([SilType.Int, value], SilType.Unit) { Consumed = [1] } : null;
        }

        // An address, as an integer: an actor's executor, as a hop names it.
        if (name == "address_to_int") {
            return operands is [AddressType] ? new([operands[0]], SilType.Int) : null;
        }

        // 'blackBox(value)'': a slot the optimiser must assume is read and
        // written, for a benchmark's work to be kept.
        if (name == BlackBox) {
            return operands is [AddressType] ? new([operands[0]], SilType.Unit) : null;
        }

        // '@immortal static let': every object the value holds made
        // immortal — never counted, never released, and no leak.
        if (name == "make_immortal") {
            return operands is [var value] ? new([value], SilType.Unit) : null;
        }

        // 'a === b': whether two references are to the one object.
        if (name == "ref_identical") {
            return operands is [ClassType, ClassType] ? new([operands[0], operands[1]], SilType.Bool) : null;
        }

        // 'x is Named', 'x is A & B': whether the object's class, or a
        // superclass, conforms to each protocol named — the conformance
        // registry (docs/06 §6.2).
        if (name.StartsWith("class_conforms:", StringComparison.Ordinal)) {
            return operands is [ClassType] ? new([operands[0]], SilType.Bool) : null;
        }

        // 'x is Dog': whether the object is of the class named, or a subclass of it.
        if (name.StartsWith("class_is:", StringComparison.Ordinal)) {
            return operands is [ClassType] ? new([operands[0]], SilType.Bool) : null;
        }

        // The dynamic exclusivity markers (docs/02 §2.3): an access to storage
        // begins, read or modify, and ends — a conflict with one in progress traps.
        if (SharedStorage.IsMarker(name)) {
            return operands is [AddressType address] ? new([address], SilType.Unit) : null;
        }

        // Room for a value in a region: its address, which the caller initialises.
        if (name == "region_alloc") {
            return operands is [var region] && region == SilType.Region && result is AddressType ? new([SilType.Region], result) : null;
        }

        // An array's or a slice's element; with 'arrayOnly', an array's.
        SilType? Element(int index, bool address, bool arrayOnly = false) {
            if (index >= operands.Count) {
                return null;
            }

            var type = address ? (operands[index] as AddressType)?.Pointee : operands[index];
            return arrayOnly ? (type as ArrayType)?.Element : ArrayType.ElementOf(type);
        }

        switch (name) {
            case "array_count" when Element(0, false) is not null:
                return new([operands[0]], SilType.Int);

            // A borrowed read leaves the element where it is: no copy, for as
            // long as the array it is in is alive and unchanged.
            case "array_get" or "array_get_borrowed" when Element(0, false) is { } element:
                return new([operands[0], SilType.Int], element);

            case "array_append" when Element(0, true, arrayOnly: true) is { } element:
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
            case "array_element_addr" or "array_element_addr_unique" when Element(0, true) is { } element:
                return new([operands[0], SilType.Int], SilType.Address(element));

            case "array_remove_last" when Element(0, true, arrayOnly: true) is { } element:
                return new([operands[0]], element);

            case "array_clear" when Element(0, true, arrayOnly: true) is not null:
                return new([operands[0]], SilType.Unit);

            // How many elements the buffer has room for, before an append
            // must grow it.
            case "array_capacity" when Element(0, false, arrayOnly: true) is not null:
                return new([operands[0]], SilType.Int);

            // Room for at least so many elements in all, the array made the
            // only reference to its buffer.
            case "array_reserve" when Element(0, true, arrayOnly: true) is not null:
                return new([operands[0], SilType.Int], SilType.Unit);

            // The first so many elements kept and the rest destroyed, the
            // buffer and its room kept: the array made the only reference to it.
            case "array_truncate" when Element(0, true, arrayOnly: true) is not null:
                return new([operands[0], SilType.Int], SilType.Unit);

            // So many copies of the element, borrowed, added at the end at
            // once: a negative count traps.
            case "array_append_repeating" when Element(0, true, arrayOnly: true) is { } repeated:
                return new([operands[0], repeated, SilType.Int], SilType.Unit);

            // So many holes added at the end at once: elements that are not
            // there, which destroying or copying the array passes over as it
            // does an object's field not set yet. One is written as any
            // element is, and read by no one before. A negative count traps.
            case "array_append_holes" when Element(0, true, arrayOnly: true) is not null:
                return new([operands[0], SilType.Int], SilType.Unit);

            // An element moved out, a hole left where it was.
            case "array_take" when Element(0, true, arrayOnly: true) is { } taken:
                return new([operands[0], SilType.Int], taken);

            // A slice's elements copied to the end at once.
            case "array_append_all" when Element(0, true, arrayOnly: true) is { } appended:
                return new([operands[0], new SliceType(appended)], SilType.Unit);

            // A string's UTF-8 bytes added at the end of a byte array at once.
            case "array_append_utf8" when Element(0, true, arrayOnly: true) == SilType.U8 && operands is [_, var text, _, _] && text == SilType.String:
                return new([operands[0], SilType.String, SilType.Int, SilType.Int], SilType.Unit);

            // Where two arrays, or slices, first differ, or the shorter's count where
            // they do not, when the element's '==' compares its bits (an
            // integer's, a Bool's, a Char's): the bytes compared, a vector
            // at a time. -1 for any other element, which only its '==' compares.
            case "array_mismatch" when Element(0, false) is { } compared && operands.Count == 2 && Element(1, false) == compared:
                return new([operands[0], operands[1]], SilType.Int);

            // The elements [from, to) of an array or a slice rotated left by
            // the third operand, in place: moved, not copied, so no element
            // is retained or released. The range is checked, as a slice's.
            case "array_rotate" when Element(0, true) is not null:
                return new([operands[0], SilType.Int, SilType.Int, SilType.Int], SilType.Unit);

            // The elements of a slice stored over [from, to) of an array or a
            // slice, where it is: as many as the range has, or a trap. A
            // slice that is still the view of that range is the elements
            // already there, and is not stored. What 'f(&xs[1..<3])' writes
            // back, and 'xs[1..<3] = s' of an array that does not grow.
            case "array_store_slice" when Element(0, true) is { } element:
                return new([operands[0], SilType.Int, SilType.Int, new SliceType(element)], SilType.Unit);

            // Elements [from, to) of an array or a slice, as a slice: a view
            // that shares the buffer, the range checked against the count.
            case "array_slice" when Element(0, false) is { } element:
                return new([operands[0], SilType.Int, SilType.Int], new SliceType(element));

            // The whole of an array, as a slice: 'sum(xs)' of a 'sum(_: Int[:])'.
            // 'slice_of_borrowed' is the same, borrowed from the array rather
            // than holding its buffer.
            case "slice_of" or "slice_of_borrowed" when Element(0, false, arrayOnly: true) is { } element:
                return new([operands[0]], new SliceType(element));

            // A slice as an array again: its buffer, when the slice is the
            // whole of it, else a copy of its run. What a mutating member of
            // 'Element[:]' hands back to the array it was called on, and
            // 'toArray()'; an array, as a member of 'Element[:]' sees one, is
            // the whole of its buffer.
            case "array_of_slice" when Element(0, false) is { } element:
                return new([operands[0]], new ArrayType(element));

            // An array, made the only reference to its buffer, moved into a
            // view of the whole of it, and the view moved back: what an
            // array's place passed to a mutating member of 'Element[:]' is
            // for the call, the buffer neither retained nor released. Back,
            // a view no longer the whole of its buffer is copied out.
            case "slice_of_take" when Element(0, false, arrayOnly: true) is { } element:
                return new([operands[0]], new SliceType(element)) { Consumed = [0] };

            case "array_of_slice_take" when operands is [SliceType { Element: var element }]:
                return new([operands[0]], new ArrayType(element)) { Consumed = [0] };

            default:
                return null;
        }
    }

    public static IEnumerable<string> Names =>
        Table.Keys.Concat(ArrayOperations).Concat(["access_begin_read", "access_begin_modify", "access_end", "access_check_read", "access_check_modify", "task_spawn", "task_wait", "task_release", "task_cancel", "task_yield", "task_sleep", "task_drain", "task_handler_push", "task_blocking", "no_context", "object_unmade", Coverage.Builtin]);

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

    /// <summary>
    ///     The numeric protocols' builtins (bit counts, rotations, overflow
    ///     reported or saturated, the floats' IEEE operations, their bits):
    ///     pure, as none traps or has an effect.
    /// </summary>
    public static bool IsNumericIntrinsic(string name) {
        var separator = name.LastIndexOf('_');
        return separator > 0 && SilType.Numbers.Any(n => n.ToString() == name[(separator + 1)..])
            && NumericIntrinsics.Contains(name[..separator]);
    }

    static readonly HashSet<string> NumericIntrinsics = [
        "count_leading_zeros", "count_trailing_zeros", "count_ones", "byte_swap", "bit_reverse", "rotate_left", "rotate_right",
        "add_overflow", "sub_overflow", "mul_overflow", "add_saturating", "sub_saturating", "mul_high",
        "sqrt", "floor", "ceil", "trunc", "next_up", "round", "round_even", "copysign", "minimum", "maximum", "remainder", "fma",
        "scale", "exponent",
    ];

    /// <summary>
    ///     The builtins over type metadata and witness tables (docs/06 §6.2):
    ///     what a type's metadata says, its value witnesses applied to a value
    ///     — a copy, a move, an assignment, a destruction, each through the
    ///     table as unspecialised code will call them — and the conformance
    ///     registry's lookups.
    /// </summary>
    static Signature? MetadataSignature(string name, IReadOnlyList<SilType> operands, SilType? result) {
        var metadata = SilType.Metadata;
        switch (name) {
            case "metadata_kind" or "metadata_size" or "metadata_alignment" or "metadata_stride" or "metadata_flags" or "metadata_field_count" or "metadata_argument_count"
                when operands is [var m] && m == metadata:
                return new([metadata], SilType.Int);
            case "metadata_name" when operands is [var m] && m == metadata:
                return new([metadata], SilType.String);
            case "metadata_field_name" when operands is [var m, var i] && m == metadata && i == SilType.Int:
                return new([metadata, SilType.Int], SilType.String);
            case "metadata_field_offset" when operands is [var m, var i] && m == metadata && i == SilType.Int:
                return new([metadata, SilType.Int], SilType.Int);
            case "metadata_field_type" or "metadata_argument" when operands is [var m, var i] && m == metadata && i == SilType.Int:
                return new([metadata, SilType.Int], metadata);
            case "metadata_equal" when operands is [var a, var b] && a == metadata && b == metadata:
                return new([metadata, metadata], SilType.Bool);
            case "value_witness_copy" when operands is [var m, var value] && m == metadata:
                return new([metadata, value], value);
            case "value_witness_take" when operands is [var m, var value] && m == metadata:
                return new([metadata, value], value) { Consumed = [1] };
            case "value_witness_assign" when operands is [var m, AddressType target, var value] && m == metadata && target.Pointee == value:
                return new([metadata, target, value], SilType.Unit);
            case "value_witness_destroy" when operands is [var m, var value] && m == metadata:
                return new([metadata, value], SilType.Unit) { Consumed = [1] };
            case "box_metadata" when operands is [ClassType box]:
                return new([box], metadata);
            case "object_metadata" when operands is [ClassType type]:
                return new([type], metadata);
            case "witness_table_exists" when operands is [var table] && table == SilType.WitnessTable:
                return new([SilType.WitnessTable], SilType.Bool);

            // What an existential's box holds, where it holds it (#299): the
            // value, borrowed, as the opaque value a witness is given; and,
            // for a witness that changes it, its address, the box first made
            // the existential's own — copied, if another existential shares it.
            case "box_project" when operands is [ClassType box, var m] && m == metadata && result is OpaqueType:
                return new([box, metadata], result);
            case "box_project_unique" when operands is [AddressType { Pointee: ClassType } slot, var m] && m == metadata && result is AddressType { Pointee: OpaqueType }:
                return new([slot, metadata], result);

            // Unspecialised code's values (#297): an address seen as holding
            // a type parameter's value or the type it is; a borrowed value's
            // own storage, seen as the type it is; its dynamic type, given its
            // metadata; and it written out, by its type's describer.
            case "opaque_cast" when operands is [AddressType from] && result is AddressType to && (SilType.MentionsOpaque(from) || SilType.MentionsOpaque(to)):
                return new([from], to);
            case "opaque_address" when operands is [var value] && SilType.IsIndirect(value) && result is AddressType:
                return new([value], result);
            // A value whose type names an opaque type seen as the same type
            // naming another, or a type known here: the same bits, borrowed
            // or handed over.
            case "opaque_borrow_as" when operands is [var viewed] && result is not null && result != viewed && SilType.SameErasingOpaque(viewed, result, opaqueOnly: true):
                return new([viewed], result!);
            case "opaque_take_as" when operands is [var taken] && result is not null && result != taken && SilType.SameErasingOpaque(taken, result, opaqueOnly: true):
                return new([taken], result!) { Consumed = [0] };
            case "metadata_array" or "metadata_slice" when operands is [var element] && element == metadata:
                return new([metadata], metadata);
            case "opaque_type" when operands is [OpaqueType value, var m] && m == metadata:
                return new([value, metadata], metadata);
            case "generic_describe" or "generic_debug_describe" when operands is [OpaqueType described, var m] && m == metadata:
                return new([described, metadata], SilType.String);
            // An error of one, boxed as 'any Error' by its type's eraser: taken.
            case "opaque_erase_error" when operands is [OpaqueType erased] && result is ClassType:
                return new([erased], result) { Consumed = [0] };
            // A call that opens a Type (#297 stage 6): a buffer its metadata
            // sizes for a result of it; that result, boxed by its type's
            // boxer, the buffer let go; and the buffer let go unwritten.
            case "opaque_alloc" when operands is [var m] && m == metadata && result is AddressType:
                return new([metadata], result);
            case "opaque_box" when operands is [AddressType buffer, var m] && m == metadata && result is ClassType:
                return new([buffer, metadata], result);
            case "opaque_free" when operands is [AddressType buffer]:
                return new([buffer], SilType.Unit);
        }

        // A requirement's witness in the generic calling convention, and an associated type's metadata.
        if (name.StartsWith("witness_generic:", StringComparison.Ordinal)) {
            return operands is [var table] && table == SilType.WitnessTable && result is FunctionType ? new([SilType.WitnessTable], result) : null;
        }

        // An opaque type's metadata, as the function finds it where it starts: an associated type's.
        if (name.StartsWith("opaque_metadata:", StringComparison.Ordinal)) {
            return operands is [] ? new([], metadata) : null;
        }

        if (name.StartsWith("witness_associated:", StringComparison.Ordinal)) {
            return operands is [var table] && table == SilType.WitnessTable ? new([SilType.WitnessTable], metadata) : null;
        }

        if (name.StartsWith("witness_table:", StringComparison.Ordinal) || name.StartsWith("witness_table_known:", StringComparison.Ordinal)
            || name.StartsWith("witness_table_required:", StringComparison.Ordinal)) {
            return operands is [var m] && m == metadata ? new([metadata], SilType.WitnessTable) : null;
        }

        // The table of a protocol a table's protocol refines, the index-th it names.
        if (name.StartsWith("witness_base:", StringComparison.Ordinal)) {
            return operands is [var table] && table == SilType.WitnessTable ? new([SilType.WitnessTable], SilType.WitnessTable) : null;
        }

        return null;
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

            // What overflow-checks = false makes of the operations that trap
            // on overflow: each keeps the low bits of the exact result.
            foreach (var op in new[] { "add", "sub", "mul" }) {
                Binary(WrappingPrefix + op, integer, integer);
            }

            Unary(WrappingPrefix + "neg", integer);
            if (SilType.IntegerInfo(integer)!.Value.Signed) {
                Unary(WrappingPrefix + "abs", integer);
            }
        }

        // What the numeric protocols (lib/Sable.Core, FixedWidthInteger)
        // are made of: the bit operations, and arithmetic that reports or
        // saturates on overflow rather than trapping. Natively each is an
        // LLVM intrinsic, or a few instructions; none traps.
        foreach (var integer in SilType.Integers) {
            // Counts of bits: as an Int, whatever the width. A zero has as
            // many leading and trailing zeros as the type has bits.
            foreach (var op in new[] { "count_leading_zeros", "count_trailing_zeros", "count_ones" }) {
                Add($"{op}_{integer}", SilType.Int, integer);
            }

            Unary("byte_swap", integer);
            Unary("bit_reverse", integer);

            // By any number of places: the count is taken modulo the width.
            Add($"rotate_left_{integer}", integer, integer, SilType.Int);
            Add($"rotate_right_{integer}", integer, integer, SilType.Int);

            // The result's low bits, and whether the exact result did not fit.
            foreach (var op in new[] { "add", "sub", "mul" }) {
                Add($"{op}_overflow_{integer}", SilType.Tuple([integer, SilType.Bool]), integer, integer);
            }

            // Clamped to the type's bounds.
            Binary("add_saturating", integer, integer);
            Binary("sub_saturating", integer, integer);

            // The high half of the product at twice the width: the low half
            // is the wrapping product.
            Binary("mul_high", integer, integer);
        }

        // The floats' IEEE 754 operations: each correctly rounded, as the
        // hardware or LLVM's intrinsic computes it.
        foreach (var real in SilType.Floats) {
            foreach (var op in new[] { "sqrt", "floor", "ceil", "trunc", "next_up" }) {
                Unary(op, real);
            }

            // To the nearest integer: a half away from zero, or to even.
            Unary("round", real);
            Unary("round_even", real);

            // The magnitude of the first with the sign of the second; the
            // smaller and the larger, a NaN either side giving NaN and -0
            // below +0 (IEEE 754-2019 minimum and maximum); and the
            // remainder of the division rounded to the nearest, ties to even.
            foreach (var op in new[] { "copysign", "minimum", "maximum", "remainder" }) {
                Binary(op, real, real);
            }

            Add($"fma_{real}", real, real, real, real);

            // x × 2ⁿ, rounded once; and, for a finite non-zero x, the n
            // with x = m × 2ⁿ and 1 <= |m| < 2 — Int.min for a zero, and
            // Int.max for an infinity or NaN, as C's 'ilogb' says.
            Add($"scale_{real}", real, real, SilType.Int);
            Add($"exponent_{real}", SilType.Int, real);
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

        // Int arithmetic whose overflow is checked later (VersionLoops): the
        // result, wrapped; and, given the iteration and the first iteration
        // that overflowed so far (Int.max for none), that first iteration.
        foreach (var op in new[] { "add", "sub", "mul" }) {
            Add($"wrapping_{op}_Int", SilType.Int, SilType.Int, SilType.Int);
            Add($"overflow_at_{op}_Int", SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int);
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

        // A number to its own type, which a builtin family converting
        // through U128 or F64 meets in U128 or F64 itself: the value.
        foreach (var number in SilType.Numbers) {
            table[$"wrap_{number}_{number}"] = new([number], number);
            table[$"convert_{number}_{number}"] = new([number], number);
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

        // Walking a string's scalars from a cursor, a byte offset the
        // iterator keeps good, so these do not check it.
        Add("string_cursor_end", SilType.Int, SilType.String);
        Add("string_cursor_scalar", SilType.Char, SilType.String, SilType.Int);
        Add("string_cursor_next", SilType.Int, SilType.String, SilType.Int);
        Add("string_cursor_previous", SilType.Int, SilType.String, SilType.Int);

        // A String.Index is a byte offset too, but one a program holds, so
        // these trap on one out of range or inside a scalar's bytes.
        Add("string_utf8_count", SilType.Int, SilType.String);
        Add("string_utf8_at", SilType.U8, SilType.String, SilType.Int);
        Add("string_utf8_load_U64", SilType.U64, SilType.String, SilType.Int);
        Add("string_index_scalar", SilType.Char, SilType.String, SilType.Int);
        Add("string_index_after", SilType.Int, SilType.String, SilType.Int);
        Add("string_index_before", SilType.Int, SilType.String, SilType.Int);
        Add("string_utf8_slice", SilType.String, SilType.String, SilType.Int, SilType.Int);

        // Where a part next occurs at or after a byte offset, or last ends at
        // or before one: a byte offset, or -1.
        Add("string_utf8_find", SilType.Int, SilType.String, SilType.String, SilType.Int);
        Add("string_utf8_find_last", SilType.Int, SilType.String, SilType.String, SilType.Int);

        // A Substring's reads: a string's bytes from one offset up to
        // another, which the library has checked are scalar boundaries
        // inside it, counted, compared, searched and hashed where they lie.
        Add("string_count_range", SilType.Int, SilType.String, SilType.Int, SilType.Int);
        Add("string_compare_range", SilType.Int, SilType.String, SilType.Int, SilType.Int, SilType.String, SilType.Int, SilType.Int);
        Add("string_utf8_find_any", SilType.Int, SilType.String, SilType.Int, SilType.Int, SilType.String);
        Add("string_utf8_find_range", SilType.Int, SilType.String, SilType.Int, SilType.Int, SilType.String, SilType.Int, SilType.Int);
        Add("string_utf8_find_last_range", SilType.Int, SilType.String, SilType.Int, SilType.Int, SilType.String, SilType.Int, SilType.Int);
        Add("string_hash_range", SilType.Int, SilType.String, SilType.Int, SilType.Int, SilType.Int);

        // Bytes as text: where the first ill-formed UTF-8 starts (-1 for
        // none), and bytes known to be well-formed made a string.
        Add("utf8_validate", SilType.Int, new SliceType(SilType.U8));
        Add("bytes_find", SilType.Int, new SliceType(SilType.U8), SilType.U8);
        Add("bytes_find_any", SilType.Int, new SliceType(SilType.U8), new SliceType(SilType.U8));
        Add("bytes_count", SilType.Int, new SliceType(SilType.U8), SilType.U8);
        Add("string_from_utf8_unchecked", SilType.String, new SliceType(SilType.U8));
        // A string being written (StringBuffer), by the address of its
        // bytes: made with room for so many, moved to more room, given a
        // run of a text's bytes, and taken as the string of the first so many,
        // which are so many scalars, or -1 for them to be counted.
        Add("string_buffer_make", SilType.Int, SilType.Int);
        Add("string_buffer_grow", SilType.Int, SilType.Int, SilType.Int, SilType.Int);
        Add("string_buffer_copy", SilType.Unit, SilType.Int, SilType.String, SilType.Int, SilType.Int);
        Add("string_buffer_valid", SilType.Bool, SilType.Int, SilType.Int);
        Add("string_buffer_take", SilType.String, SilType.Int, SilType.Int, SilType.Int);
        // The first 'n' bytes of five words, each little-endian, which are
        // ASCII and at most forty: short text made in registers.
        Add("string_from_ascii_words", SilType.String, SilType.U64, SilType.U64, SilType.U64, SilType.U64, SilType.U64, SilType.Int);
        Add("string_trimmed", SilType.String, SilType.String);
        Add("string_split", new ArrayType(SilType.String), SilType.String, SilType.Char);
        // A string's bytes hashed under a key (folded multiplies), and the key every
        // Hasher starts from: random per process unless fixed by
        // SABLE_DETERMINISTIC_HASHING=1.
        Add("string_hash", SilType.Int, SilType.String, SilType.Int);
        Add("hash_seed", SilType.Int);
        // 64 bits from the operating system's random source, fresh at each call.
        Add("random_bits", SilType.Int);
        // Sable.Crypto's: a slice of bytes, or so many bytes at an address,
        // filled from that source, or set to zero whether or not anything
        // reads them after; and whole blocks of a hash (the algorithm, its
        // state's address, the bytes) worked into its state by the
        // processor's own instructions, or false when it has none.
        Add("bytes_random", SilType.Unit, SilType.Address(new SliceType(SilType.U8)));
        Add("bytes_secure_zero", SilType.Unit, SilType.Address(new SliceType(SilType.U8)));
        Add("raw_random", SilType.Unit, SilType.Int, SilType.Int);
        Add("raw_secure_zero", SilType.Unit, SilType.Int, SilType.Int);
        // Blocks of Base64 or Hex (where they go, the codec, the source), by
        // the processor's own instructions: how much of the source was done.
        Add("codec_bytes_bytes", SilType.Int, SilType.Address(new SliceType(SilType.U8)), SilType.Int, new SliceType(SilType.U8), SilType.Int);
        Add("codec_string_bytes", SilType.Int, SilType.Address(new SliceType(SilType.U8)), SilType.Int, SilType.String, SilType.Int, SilType.Int, SilType.Int);
        Add("utf8_to_utf16", SilType.Int, SilType.Address(new SliceType(SilType.U16)), new SliceType(SilType.U8), SilType.Int);
        Add("utf16_to_utf8", SilType.Int, SilType.Address(new SliceType(SilType.U8)), new SliceType(SilType.U16), SilType.Int);
        Add("codec_bytes_raw", SilType.Int, SilType.Int, new SliceType(SilType.U8), SilType.Int, SilType.Int);
        Add("crypto_blocks", SilType.Bool, SilType.Int, SilType.Int, new SliceType(SilType.U8));
        Add("crypto_blocks_raw", SilType.Bool, SilType.Int, SilType.Int, SilType.Int, SilType.Int);
        Add("crypto_cipher", SilType.Bool, SilType.Address(new SliceType(SilType.U8)), SilType.Int, SilType.Int, SilType.Int, new SliceType(SilType.U8), new SliceType(SilType.U8));
        Add("crypto_cipher_in_place", SilType.Bool, SilType.Address(new SliceType(SilType.U8)), SilType.Int, SilType.Int, SilType.Int, new SliceType(SilType.U8));
        Add("crypto_cipher_raw", SilType.Bool, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int);
        // Sable.Math's kernels over arrays of floats, where the processor has
        // more than the target's baseline: whether it has, a reduction of one
        // slice or two by its number (the sum, dot product or cosine), and an
        // elementwise operation on two to the first elements of a third.
        Add("vector_available", SilType.Bool);
        foreach (var real in new[] { SilType.F32, SilType.F64 }) {
            var reals = new SliceType(real);
            Add($"vector_reduce_{real}", real, SilType.Int, reals, reals);
            Add($"vector_map_{real}", SilType.Unit, SilType.Address(reals), SilType.Int, reals, reals);
        }

        Add("string_join", SilType.String, new ArrayType(SilType.String), SilType.String);
        Add("string_compare", SilType.Int, SilType.String, SilType.String);
        // Where a part next occurs in a text, at or after a scalar position: a scalar index, or -1.
        Add("string_find", SilType.Int, SilType.String, SilType.String, SilType.Int);
        Add("string_from_Char", SilType.String, SilType.Char);
        foreach (var number in SilType.Numbers) {
            Add($"string_from_{number}", SilType.String, number);
        }

        Add("string_from_Bool", SilType.String, SilType.Bool);
        Add("select_Int", SilType.Int, SilType.Bool, SilType.Int, SilType.Int);

        // Parsing gives whether it worked, and the value when it did.
        Add("string_to_Int", SilType.Tuple([SilType.Bool, SilType.Int]), SilType.String);
        // A float is read from a range of the string's bytes: a Substring's.
        Add("string_to_F64", SilType.Tuple([SilType.Bool, SilType.F64]), SilType.String, SilType.Int, SilType.Int);
        Add("string_to_F32", SilType.Tuple([SilType.Bool, SilType.F32]), SilType.String, SilType.Int, SilType.Int);

        // The runtime's calls (Sable.IO's floor). Those that can fail give a
        // value either way; 'io_succeeded' then says whether the last one did.
        Add("io_write", SilType.Unit, SilType.String);
        Add("io_write_error", SilType.Unit, SilType.String);

        // Stopping the program with a message and the place it was stopped
        // from, both made as it runs (fatalError, precondition); and the
        // frames of the calls that led here, a line each.
        Add("trap", SilType.Unit, SilType.String, SilType.String);
        Add("backtrace_capture", new ArrayType(SilType.String));
        // An error main threw, stopped at as a trap is, but with status 1.
        Add("trap_uncaught", SilType.Unit, SilType.String, SilType.String);
        // The panic hook (Panic.setHook): a closure given a trap's message,
        // place and frames, which the runtime holds; set, or taken out with
        // the default given back in its place when there is none.
        var trapHook = new ThickFunctionType(new FunctionType([SilType.String, SilType.String, new ArrayType(SilType.String)], SilType.Unit));
        Add("trap_hook_set", SilType.Unit, trapHook);
        Add("trap_hook_take", trapHook, trapHook);
        // Whether a debugger is attached; and stopping for it, if one is.
        Add("debugger_attached", SilType.Bool);
        Add("debugger_break", SilType.Unit);
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
        Add("io_argument_bytes", new ArrayType(SilType.U8), SilType.Int);
        Add("env_set", SilType.Bool, SilType.String, SilType.String);
        Add("env_remove", SilType.Bool, SilType.String);
        Add("env_all", new ArrayType(SilType.String));
        Add("system_text", SilType.String, SilType.Int);
        Add("system_number", SilType.Int, SilType.Int);
        Add("memory_statistic", SilType.Int, SilType.Int);
        Add("io_succeeded", SilType.Bool);
        // Files by handle (runtime/io.c, "File handles"): a call that fails
        // gives -1, nothing or false, and 'file_error' says why by IoError's
        // kinds, 'file_error_code' by the system's own code.
        Add("file_error", SilType.Int);
        Add("file_error_code", SilType.Int);
        Add("file_open", SilType.Int, SilType.String, SilType.Int, SilType.Int);
        Add("file_read", new ArrayType(SilType.U8), SilType.Int, SilType.Int, SilType.Int);
        Add("file_write", SilType.Int, SilType.Int, new SliceType(SilType.U8), SilType.Int);
        Add("file_seek", SilType.Int, SilType.Int, SilType.Int, SilType.Int);
        Add("file_sync", SilType.Bool, SilType.Int);
        Add("file_set_length", SilType.Bool, SilType.Int, SilType.Int);
        Add("file_metadata", new ArrayType(SilType.Int), SilType.Int);
        Add("file_close", SilType.Bool, SilType.Int);
        Add("file_standard", SilType.Int, SilType.Int);
        Add("file_copy", SilType.Bool, SilType.String, SilType.String, SilType.Bool);
        Add("file_canonical", SilType.String, SilType.String);
        Add("file_map", new ArrayType(SilType.U8), SilType.Int, SilType.Int, SilType.Int);
        Add("file_map_store", SilType.Bool, new ArrayType(SilType.U8), SilType.Int, new SliceType(SilType.U8));
        Add("file_map_flush", SilType.Bool, new ArrayType(SilType.U8), SilType.Int, SilType.Int);
        Add("file_map_advise", SilType.Unit, new ArrayType(SilType.U8), SilType.Int);

        // The file system by path (runtime/fs.c), failing as a file handle's
        // calls do; and files watched as they change (runtime/watch.c): a
        // watch, what to wait for ('task_io_wait') when none is to be taken.
        Add("fs_metadata", new ArrayType(SilType.Int), SilType.String, SilType.Bool);
        Add("fs_set_permissions", SilType.Bool, SilType.String, SilType.Int);
        Add("fs_set_time", SilType.Bool, SilType.String, SilType.Int, SilType.Int, SilType.Int);
        Add("fs_make_directory", SilType.Bool, SilType.String, SilType.Int);
        Add("fs_remove", SilType.Bool, SilType.String, SilType.Bool);
        Add("fs_remove_tree", SilType.Bool, SilType.String);
        Add("fs_rename", SilType.Bool, SilType.String, SilType.String, SilType.Bool);
        Add("fs_link", SilType.Bool, SilType.String, SilType.String, SilType.Int);
        Add("fs_read_link", new ArrayType(SilType.U8), SilType.String);
        Add("fs_open_directory", SilType.Int, SilType.String);
        Add("fs_next_entry", new ArrayType(SilType.U8), SilType.Int);
        Add("fs_close_directory", SilType.Unit, SilType.Int);
        Add("fs_watch", SilType.Int, SilType.String, SilType.Bool);
        Add("fs_watch_event", SilType.Int, SilType.Int);
        Add("fs_watch_take", new ArrayType(SilType.U8), SilType.Int);
        Add("fs_unwatch", SilType.Unit, SilType.Int);
        Add("time_monotonic", SilType.Int);
        Add("time_suspending", SilType.Int);
        Add("time_wall_clock", SilType.Int);
        Add("time_sleep", SilType.Unit, SilType.Int);

        // Sockets (runtime/net.c), by handle, every one nonblocking: a call
        // that would block says so ('net_status' 1, failed 2) and the
        // library waits for the socket ('task_io_wait': a place a task
        // suspends, AsyncLowering) before calling again.
        Add("net_status", SilType.Int);
        Add("net_error", SilType.Int);
        Add("net_error_code", SilType.Int);
        Add("net_address", SilType.Int, SilType.Int);
        Add("net_path", SilType.String);
        Add("net_accept", SilType.Int, SilType.Int);
        Add("net_connected", SilType.Bool, SilType.Int);
        Add("net_read", new ArrayType(SilType.U8), SilType.Int, SilType.Int);
        Add("net_write", SilType.Int, SilType.Int, new SliceType(SilType.U8));
        Add("net_receive", new ArrayType(SilType.U8), SilType.Int, SilType.Int);
        Add("net_name", SilType.Bool, SilType.Int, SilType.Bool);
        Add("net_set_option", SilType.Bool, SilType.Int, SilType.Int, SilType.Int);
        Add("net_option", SilType.Int, SilType.Int, SilType.Int);
        Add("net_membership", SilType.Bool, SilType.Int, SilType.Bool, SilType.Int, SilType.Int, SilType.Int, SilType.Int);
        Add("net_shutdown", SilType.Unit, SilType.Int, SilType.Bool);
        Add("net_close", SilType.Unit, SilType.Int);

        // An address is its family (4, 6; 1, a Unix domain socket's), its
        // halves, its scope, its port and its path.
        Add("net_listen", SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.String, SilType.Int, SilType.Int);
        Add("net_connect", SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.String);
        Add("net_datagram", SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.String, SilType.Int);
        Add("net_connect_datagram", SilType.Bool, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.String);
        Add("net_send", SilType.Int, SilType.Int, new SliceType(SilType.U8), SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.Int, SilType.String);

        // Names looked up, and the machine's interfaces.
        Add("net_resolve", SilType.Int, SilType.String, SilType.Int);
        Add("net_resolve_event", SilType.Int, SilType.Int);
        Add("net_resolved", SilType.Int, SilType.Int);
        Add("net_resolved_take", SilType.Unit, SilType.Int, SilType.Int);
        Add("net_resolve_end", SilType.Unit, SilType.Int);
        Add("net_interfaces", SilType.Int, SilType.Bool);
        Add("net_interface_name", SilType.String, SilType.Int);
        Add("net_interface", SilType.Int, SilType.Int, SilType.Int);
        Add("net_interface_index", SilType.Int, SilType.String);
        Add("task_io_wait", SilType.Unit, SilType.Int, SilType.Bool);

        // Tasks (runtime/tasks.c): whether the running one is cancelled, its
        // priority; and groups, by handle — made, given a child just spawned,
        // waited on for a child to finish (a place a task suspends,
        // AsyncLowering), the finished child taken, and freed.
        Add("task_is_cancelled", SilType.Bool);
        Add("task_priority", SilType.Int);
        Add("task_handler_pop", SilType.Unit);

        // Actors (runtime/tasks.c): where the running task runs — an actor
        // object's identity, 1 for the main actor, 0 for anywhere — and a
        // hop there, a place the task may suspend (AsyncLowering). Wait
        // lists, by handle: made, freed, every task in one woken, and the
        // running task parked in one until then.
        Add("task_actor_current", SilType.Int);

        // The next task the running thread spawns starts cancelled: a
        // cancelled group's child, before any thread can run it.
        Add("task_spawn_cancelled", SilType.Unit);

        // A wait for a child with a limit (withTimeout): begun; whether the
        // child has ended; and, the child's, that it has.
        Add("task_timed_begin", SilType.Unit);
        Add("task_timed_woken", SilType.Bool);
        Add("task_timed_end", SilType.Unit);
        Add("task_hop", SilType.Unit, SilType.Int);
        Add("task_waitlist_new", SilType.Int);
        Add("task_waitlist_free", SilType.Unit, SilType.Int);
        Add("task_waitlist_wake", SilType.Unit, SilType.Int);
        Add("task_waitlist_wake_one", SilType.Unit, SilType.Int);
        Add("task_waitlist_grant", SilType.Unit, SilType.Int);
        Add("task_park", SilType.Unit, SilType.Int);

        // A wait list's ticket — how many times it has been woken — and a
        // park that a wake since the ticket (-1: none), or the running
        // task's cancellation, ends or prevents (WaitList.parkCancellably).
        Add("task_waitlist_ticket", SilType.Int, SilType.Int);
        Add("task_park_cancellable", SilType.Unit, SilType.Int, SilType.Int);
        Add("task_group_new", SilType.Int);
        Add("task_group_add", SilType.Unit, SilType.Int, SilType.Int);
        Add("task_group_wait", SilType.Unit, SilType.Int);
        Add("task_group_take", SilType.Int, SilType.Int);
        Add("task_group_free", SilType.Unit, SilType.Int);
        Add("process_run", SilType.String, SilType.String, new ArrayType(SilType.String), SilType.String);
        Add("process_status", SilType.Int);
        Add("process_status_signal", SilType.Int);
        Add("process_error_output", SilType.String);
        Add("process_error", SilType.Int);
        Add("process_error_code", SilType.Int);
        Add("process_spawn", SilType.Int, SilType.String, new ArrayType(SilType.String), new ArrayType(SilType.String), SilType.Int, SilType.String, new ArrayType(SilType.String));
        Add("process_pipe", SilType.Int, SilType.Int, SilType.Int);
        Add("process_child_id", SilType.Int, SilType.Int);
        Add("process_ended", SilType.Bool, SilType.Int);
        Add("process_exit_code", SilType.Int, SilType.Int, SilType.Bool);
        Add("process_exit_event", SilType.Int, SilType.Int);
        Add("process_signal", SilType.Int, SilType.Int, SilType.Int);
        Add("process_release", SilType.Unit, SilType.Int);
        Add("process_exit", SilType.Unit, SilType.Int);
        Add("process_abort", SilType.Unit);
        Add("signal_watch", SilType.Int, SilType.Int);
        Add("signal_event", SilType.Int, SilType.Int);
        Add("signal_take", SilType.Int, SilType.Int);
        Add("signal_unwatch", SilType.Unit, SilType.Int);
        Add("signal_raise", SilType.Bool, SilType.Int);
        Add("process_current_directory", SilType.String);
        Add("process_change_directory", SilType.Bool, SilType.String);
        Add("process_id", SilType.Int);
        Add("process_notify", SilType.Int, SilType.String, SilType.String);
        Add("memory_allocations", SilType.Int);
        Add("process_executable_path", SilType.String);

        // What a type's metadata says (docs/06 §6.2): MetadataSignature has the rest of the metadata builtins.
        foreach (var question in new[] { "metadata_kind", "metadata_size", "metadata_alignment", "metadata_stride", "metadata_flags", "metadata_field_count", "metadata_argument_count" }) {
            Add(question, SilType.Int, SilType.Metadata);
        }

        Add("metadata_name", SilType.String, SilType.Metadata);
        Add("metadata_field_name", SilType.String, SilType.Metadata, SilType.Int);
        Add("metadata_field_offset", SilType.Int, SilType.Metadata, SilType.Int);
        Add("metadata_field_type", SilType.Metadata, SilType.Metadata, SilType.Int);
        Add("metadata_argument", SilType.Metadata, SilType.Metadata, SilType.Int);
        Add("metadata_equal", SilType.Bool, SilType.Metadata, SilType.Metadata);
        Add("metadata_hash", SilType.U64, SilType.Metadata);
        Add("metadata_is_subtype", SilType.Bool, SilType.Metadata, SilType.Metadata);

        Add("region_create", SilType.Region);

        // Raw memory (RawPtr, NativeMemory): addresses as Ints. Room of so
        // many bytes at an alignment, freed again; bytes moved, as memmove
        // does, and filled with one byte. What a value of a type does at an
        // address is the generic raw_load, raw_take and raw_init's (Resolve).
        Add("raw_alloc", SilType.Int, SilType.Int, SilType.Int);
        Add("raw_free", SilType.Unit, SilType.Int);
        Add("raw_copy", SilType.Unit, SilType.Int, SilType.Int, SilType.Int);
        Add("raw_fill", SilType.Unit, SilType.Int, SilType.U8, SilType.Int);

        // Atomics (Atomic<Value>): each on the value at a raw address, with
        // a memory ordering as an Int — 0 relaxed, 1 acquire, 2 release, 3
        // acquire-release, 4 sequentially consistent — which code generation
        // switches on, and which is folded once it is a constant. Every
        // integer up to 64 bits, and Bool; a read-modify-write gives the
        // value it found, and a compare-exchange that and whether it stored.
        foreach (var atomic in SilType.AtomicTypes) {
            Add($"atomic_load_{atomic}", atomic, SilType.Int, SilType.Int);
            Add($"atomic_store_{atomic}", SilType.Unit, SilType.Int, atomic, SilType.Int);
            Add($"atomic_exchange_{atomic}", atomic, SilType.Int, atomic, SilType.Int);
            Add($"atomic_compare_exchange_{atomic}", SilType.Tuple([atomic, SilType.Bool]), SilType.Int, atomic, atomic, SilType.Int);
            Add($"atomic_compare_exchange_weak_{atomic}", SilType.Tuple([atomic, SilType.Bool]), SilType.Int, atomic, atomic, SilType.Int);
            var operations = atomic == SilType.Bool ? new[] { "and", "or", "xor" } : new[] { "add", "sub", "and", "or", "xor", "min", "max" };
            foreach (var op in operations) {
                Add($"atomic_{op}_{atomic}", atomic, SilType.Int, atomic, SilType.Int);
            }
        }

        Add("atomic_fence", SilType.Unit, SilType.Int);

        // Locks (Mutex, Condition and what is built on them), by handle. A thread that waits
        // blocks. A condition's wait takes the mutex held, and a time of the
        // monotonic clock to stop at, or a negative one for none.
        Add("lock_new", SilType.Int);
        Add("lock_free", SilType.Unit, SilType.Int);
        Add("lock_acquire", SilType.Unit, SilType.Int);
        Add("lock_acquire_later", SilType.Unit, SilType.Int);
        Add("lock_try_acquire", SilType.Bool, SilType.Int);
        Add("lock_release", SilType.Unit, SilType.Int);
        // A reader/writer lock: what reading it gives, its end takes.
        Add("rwlock_new", SilType.Int);
        Add("rwlock_free", SilType.Unit, SilType.Int);
        Add("rwlock_read", SilType.Int, SilType.Int);
        Add("rwlock_read_done", SilType.Unit, SilType.Int, SilType.Int);
        Add("rwlock_write", SilType.Unit, SilType.Int);
        Add("rwlock_write_done", SilType.Unit, SilType.Int);
        Add("condition_new", SilType.Int);
        Add("condition_free", SilType.Unit, SilType.Int);
        Add("condition_wait", SilType.Unit, SilType.Int, SilType.Int, SilType.Int);
        Add("condition_signal", SilType.Unit, SilType.Int);
        Add("condition_broadcast", SilType.Unit, SilType.Int);

        // Emptying a region, all of it or back to a mark: what a 'region frame = pool' block took.
        Add("region_reset", SilType.Unit, SilType.Region);
        Add("region_push", SilType.Unit, SilType.Region);
        Add("region_pop", SilType.Unit, SilType.Region);
        Add("char_to_Int", SilType.Int, SilType.Char);
        Add("char_from_Int", SilType.Tuple([SilType.Bool, SilType.Char]), SilType.Int);
        Add("char_is_digit", SilType.Bool, SilType.Char);

        // sin, exp, pow, …: libm's, for F64 and F32.
        foreach (var (name, signature) in FloatFunctions.Signatures()) {
            table[name] = signature;
        }

        // An integer read from, or written to, bytes at an offset, in the
        // platform's order (little-endian everywhere): one range check and
        // one unaligned load or store, rather than a byte at a time. A read
        // is of any slice of bytes; a write, of a slice in its place (any
        // array is viewed as one for a mutating slice member), made the only
        // reference to its run first.
        foreach (var unsigned in new[] { SilType.U16, SilType.U32, SilType.U64 }) {
            Add($"bytes_load_{unsigned}", unsigned, new SliceType(SilType.U8), SilType.Int);
            Add($"bytes_load_array_{unsigned}", unsigned, new ArrayType(SilType.U8), SilType.Int);
            Add($"bytes_store_{unsigned}", SilType.Unit, SilType.Address(new SliceType(SilType.U8)), SilType.Int, unsigned);
            Add($"bytes_store_unique_{unsigned}", SilType.Unit, SilType.Address(new SliceType(SilType.U8)), SilType.Int, unsigned);
            // The same, of an array of bytes in its place (HoistUniqueness).
            Add($"bytes_store_array_{unsigned}", SilType.Unit, SilType.Address(new ArrayType(SilType.U8)), SilType.Int, unsigned);
            Add($"bytes_store_array_unique_{unsigned}", SilType.Unit, SilType.Address(new ArrayType(SilType.U8)), SilType.Int, unsigned);
        }

        // A float's bits as the unsigned integer of its width, and back: the
        // same bits, not the same number (a NaN's payload is kept).
        foreach (var (real, bits) in new[] { (SilType.F64, SilType.U64), (SilType.F32, SilType.U32), (SilType.F16, SilType.U16), (SilType.BF16, SilType.U16) }) {
            Add($"bitcast_{real}_{bits}", bits, real);
            Add($"bitcast_{bits}_{real}", real, bits);
        }

        return table;
    }
}
