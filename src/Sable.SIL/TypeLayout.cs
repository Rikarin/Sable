using System.Runtime.CompilerServices;

namespace Sable.SIL;

/// <summary>
///     Where a value of each SIL type is in memory: its size, its alignment,
///     its stride and where its parts are. The code generator lays values out
///     by it, and type metadata (<see cref="TypeMetadata" />) reports it, so
///     that a native program and the interpreter give the same numbers.
/// </summary>
/// <remarks>
///     <para>
///         Most aggregates are laid out as C lays out a struct: each part at the
///         next offset its alignment allows, the whole rounded up to the
///         largest alignment. <c>@packed</c> and <c>@align(n)</c> (SYNTAX.md
///         §22) change the rule: a packed struct's parts follow one another
///         with no padding, and an over-aligned one starts at a multiple of its
///         alignment wherever it is held. Such an aggregate, or one holding one,
///         is <see cref="Aggregate.Explicit" />: the code generator writes its
///         padding out, since LLVM's literal structs cannot say it.
///     </para>
///     <para>
///         An enum is laid out in the least room its cases allow. An optional
///         reference — one case holding a class reference or a string, one
///         holding nothing — is the reference itself, null for none. One case
///         holding a Bool or a Char and the rest nothing is that scalar, each
///         other case one of the values it never takes (its niche): 'Bool?' is a
///         byte with none 2, 'Char?' four bytes with none U+110000. Anything
///         else is a tag — a byte, unless there are more than 256 cases — and
///         room for the largest payload, in units of the most aligned one,
///         after the tag.
///     </para>
/// </remarks>
public static class TypeLayout {
    /// <summary>How an aggregate is laid out.</summary>
    /// <param name="Offsets">For each part, its offset in bytes.</param>
    /// <param name="Size">Its size, a multiple of its alignment.</param>
    /// <param name="Align">Its alignment.</param>
    /// <param name="Explicit">Whether it needs its padding written out: packed, over-aligned, or holding such a part.</param>
    public sealed record Aggregate(int[] Offsets, int Size, int Align, bool Explicit);

    /// <summary>A scalar with values to spare, holding an enum: the case with the payload, the payload, its size, and the first value no payload takes.</summary>
    public sealed record NicheLayout(int Some, SilType Payload, int Bytes, long Spare) {
        /// <summary>The value a case without a payload is: the spare values, in case order.</summary>
        public long ValueOf(EnumType type, int @case) => Spare + type.Cases.Take(@case).Count(static c => c.Payload is null);
    }

    [ThreadStatic] static int _pointerBytes;

    /// <summary>The bytes of a pointer on the target being compiled for: 4 on WebAssembly, 8 elsewhere (and in the interpreter).</summary>
    public static int PointerBytes {
        get => _pointerBytes == 0 ? 8 : _pointerBytes;
        set => _pointerBytes = value;
    }

    /// <summary>The bytes of the header every counted object starts with (docs/06 §6.2): its metadata, its count, its flags.</summary>
    public static int ObjectHeaderSize => PointerBytes + 8;

    /// <summary>How the runtime aligns every block it allocates.</summary>
    public const int HeapAlign = 16;

    static readonly ConditionalWeakTable<SilType, Aggregate> Aggregates = new();
    static readonly ConditionalWeakTable<ClassType, Aggregate> Objects = new();

    /// <summary>Bytes a value of the type occupies, as it is stored.</summary>
    public static int Size(SilType type) =>
        type switch {
            _ when type == SilType.Bool => 1,
            _ when type == SilType.Int || type == SilType.F64 => 8,
            _ when type == SilType.F32 => 4,
            _ when type == SilType.F16 || type == SilType.BF16 => 2,
            _ when SilType.IntegerInfo(type) is var (bits, _) => bits / 8,
            _ when type == SilType.Char => 4,
            _ when type == SilType.String || type == SilType.Metadata => PointerBytes,
            AddressType or FunctionType or ClassType or ArrayType => PointerBytes,
            StructType { IsInline: true } or InlinePointerType => PointerBytes,
            _ when type == SilType.Object || type == SilType.Region => PointerBytes,
            OpaqueType => PointerBytes,
            ThickFunctionType => 2 * PointerBytes,
            WeakStorageType or UnownedStorageType => PointerBytes,
            EnumType enumType when IsNullable(enumType) => PointerBytes,
            EnumType enumType when Niche(enumType) is { } niche => niche.Bytes,
            EnumType enumType => TaggedSize(enumType),
            StructType structType => Of(structType).Size,
            TupleType tuple => Of(tuple).Size,
            SliceType slice => Size(slice.Parts),
            VectorType vector => RoundUp(VectorBytes(vector), Align(vector)),
            _ => 0,
        };

    /// <summary>
    ///     The bytes a vector's lanes take, side by side: a Bool lane is a
    ///     bit. LLVM stores <c>&lt;N x T&gt;</c> in that many, aligned to the
    ///     power of two at or above it — its natural alignment, which every
    ///     target Sable has leaves as it is — and rounds its size up to that.
    /// </summary>
    static int VectorBytes(VectorType vector) => (vector.Count * (vector.Element == SilType.Bool ? 1 : Size(vector.Element) * 8) + 7) / 8;

    public static int Align(SilType type) =>
        type switch {
            _ when type == SilType.Bool => 1,
            _ when type == SilType.Char => 4,
            _ when type == SilType.F32 => 4,
            _ when type == SilType.F16 || type == SilType.BF16 => 2,
            _ when SilType.IntegerInfo(type) is var (bits, _) => bits / 8,
            _ when type == SilType.Int || type == SilType.F64 => 8,
            _ when type == SilType.Unit => 1,
            StructType { IsInline: true } => PointerBytes,
            StructType structType => Of(structType).Align,
            TupleType tuple => Of(tuple).Align,
            EnumType enumType when IsNullable(enumType) => PointerBytes,
            EnumType enumType when Niche(enumType) is { } niche => niche.Bytes,
            EnumType enumType => Math.Max(Area(enumType).Unit, TagBytes(enumType)),
            VectorType vector => (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)VectorBytes(vector)),
            _ => PointerBytes,
        };

    /// <summary>The distance from one value of the type to the next in an array: its size rounded up to its alignment (0 for a type with no bytes, as an array of them has none).</summary>
    public static int Stride(SilType type) => RoundUp(Size(type), Align(type));

    /// <summary>
    ///     The alignment a part is laid out with when its aggregate is written
    ///     as LLVM lays a literal struct out: a byte for an explicit one, whose
    ///     padding is written out and which LLVM therefore thinks unaligned.
    /// </summary>
    public static int WrittenAlign(SilType type) =>
        type switch {
            StructType { IsInline: false } structType => Of(structType).Explicit ? 1 : Align(type),
            TupleType tuple => Of(tuple).Explicit ? 1 : Align(type),
            _ => Align(type),
        };

    /// <summary>What a struct is laid out as: its fields, after a hidden live byte for one with a deinit.</summary>
    public static IEnumerable<SilType> StructParts(StructType type) =>
        type.Deinit is null ? type.Fields.Select(static f => f.Type) : [SilType.Bool, ..type.Fields.Select(static f => f.Type)];

    /// <summary>Where a struct's field is among its parts: one further along when there is a live byte.</summary>
    public static int PartIndex(StructType type, int field) => type.Deinit is null ? field : field + 1;

    /// <summary>A struct's layout: its parts, a live byte first for one with a deinit.</summary>
    public static Aggregate Of(StructType type) =>
        Aggregates.GetValue(type, static t => {
                var structType = (StructType)t;
                return LayOut([..StructParts(structType)], structType.IsPacked, structType.Alignment);
            }
        );

    public static Aggregate Of(TupleType type) => Aggregates.GetValue(type, static t => LayOut([..((TupleType)t).Elements], false, 0));

    /// <summary>The parts of an object as laid out: the header — its type, its count, its flags — then its fields.</summary>
    public static List<SilType> ObjectParts(ClassType type) => [SilType.Object, SilType.U32, SilType.U32, ..type.Fields.Select(static f => f.Type)];

    /// <summary>An object's layout: the header, then the fields (whose offsets are from index 3 on).</summary>
    public static Aggregate Object(ClassType type) => Objects.GetValue(type, static t => LayOut(ObjectParts(t), false, 0));

    /// <summary>Where a field of an object is, from its start, the header's bytes among them.</summary>
    public static int FieldOffset(ClassType type, int field) => Object(type).Offsets[field + 3];

    /// <summary>Where a field of a struct is, from its start.</summary>
    public static int FieldOffset(StructType type, int field) => Of(type).Offsets[PartIndex(type, field)];

    public static Aggregate LayOut(List<SilType> parts, bool packed, int alignment) {
        var natural = parts.Select(Align).DefaultIfEmpty(1).Max();
        var align = packed ? Math.Max(1, alignment) : Math.Max(natural, alignment);
        var offsets = new int[parts.Count];
        var explicitly = packed || alignment > natural || !parts.TrueForAll(static p => WrittenAlign(p) == Align(p));
        var end = 0;
        for (var i = 0; i < parts.Count; i++) {
            var at = packed ? end : RoundUp(end, Align(parts[i]));
            offsets[i] = at;
            end = at + Size(parts[i]);
        }

        return new(offsets, RoundUp(end, align), align, explicitly);
    }

    public static int RoundUp(int offset, int align) => (offset + align - 1) / align * align;

    public static bool IsNullable(EnumType type) =>
        type.Cases.Count == 2
        && type.Cases[1 - SomeCase(type)].Payload is null
        && type.Cases[SomeCase(type)].Payload is { } payload && (payload is ClassType || payload == SilType.String);

    public static int SomeCase(EnumType type) => type.Cases[0].Payload is null ? 1 : 0;

    public static NicheLayout? Niche(EnumType type) {
        var payloads = type.Cases.Select(static (c, i) => (c.Payload, i)).Where(static c => c.Payload is not null).ToList();
        if (payloads is not [var (payload, some)] || payload!.IsTrivial is false) {
            return null;
        }

        var empty = type.Cases.Count - 1;
        return payload == SilType.Bool && empty <= 254 ? new(some, payload, 1, 2)
            : payload == SilType.Char && empty <= 0x7FFFFFFF - 0x110000 ? new(some, payload, 4, 0x110000)
            : null;
    }

    /// <summary>A tagged enum's tag: a byte, or four for more than 256 cases.</summary>
    public static int TagBytes(EnumType type) => type.Cases.Count <= 256 ? 1 : 4;

    /// <summary>A tagged enum's payload area: so many units of the most aligned payload's alignment, room for the largest.</summary>
    public static (int Count, int Unit) Area(EnumType type) {
        var payloads = type.Cases.Where(static c => c.Payload is not null).Select(static c => c.Payload!).ToList();
        var size = payloads.Select(Size).DefaultIfEmpty(0).Max();
        if (size == 0) {
            return (0, 1);
        }

        var unit = payloads.Select(Align).Max();
        return ((size + unit - 1) / unit, unit);
    }

    /// <summary>Where a tagged enum's payload area starts: after the tag, aligned for the area.</summary>
    public static int AreaOffset(EnumType type) {
        var (_, unit) = Area(type);
        return (TagBytes(type) + unit - 1) / unit * unit;
    }

    /// <summary>Where a case's payload is in a value of the enum: at the start when the enum is the payload itself (a nullable reference, a niche), else after the tag.</summary>
    public static int PayloadOffset(EnumType type) => IsNullable(type) || Niche(type) is not null ? 0 : AreaOffset(type);

    public static int TaggedSize(EnumType type) {
        var (count, unit) = Area(type);
        var align = Math.Max(unit, TagBytes(type));
        var end = count == 0 ? TagBytes(type) : AreaOffset(type) + count * unit;
        return (end + align - 1) / align * align;
    }
}
