using System.Runtime.CompilerServices;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     Where the parts of a struct, a tuple and an object are in memory.
/// </summary>
/// <remarks>
///     <para>
///         Most aggregates are laid out as LLVM lays out a literal struct,
///         <c>{ i8, i64 }</c>: each part at the next offset its alignment
///         allows, the whole rounded up to the largest alignment. That is
///         also C's rule, so a struct passed to C is the struct C declares.
///     </para>
///     <para>
///         <c>@packed</c> and <c>@align(n)</c> (SYNTAX.md §22) change the rule,
///         which LLVM's literal structs cannot say: a packed struct's parts
///         follow one another with no padding, and an over-aligned struct
///         starts at a multiple of its alignment, wherever it is held. Such an
///         aggregate, and one that holds such a part, is written as a packed
///         LLVM struct, <c>&lt;{ i8, [7 x i8], i64 }&gt;</c>, with the padding
///         as byte arrays at the offsets computed here; its parts' indices in
///         the LLVM type are then not their own, which <see cref="AggregateLayout.Slots" />
///         maps. LLVM thinks a packed struct aligned to a byte, so storage for
///         one is given its alignment explicitly, and an access through a part
///         of a packed struct that may be misaligned says how aligned it is.
///     </para>
///     <para>
///         Heap blocks are aligned to 16 bytes, so <c>@align</c> is at most 16
///         (S3510): every place a value can be — a frame, a global, a field, an
///         element, an object, a payload — is then aligned as it says.
///     </para>
/// </remarks>
public sealed partial class LlvmEmitter {
    /// <summary>How an aggregate is laid out.</summary>
    /// <param name="Llvm">Its LLVM type.</param>
    /// <param name="Slots">For each part, its index in the LLVM type.</param>
    /// <param name="Offsets">For each part, its offset in bytes.</param>
    /// <param name="Size">Its size, a multiple of its alignment.</param>
    /// <param name="Align">Its alignment.</param>
    /// <param name="Explicit">Whether it is written as a packed LLVM struct, padding and all.</param>
    sealed record AggregateLayout(string Llvm, int[] Slots, int[] Offsets, int Size, int Align, bool Explicit);

    static readonly ConditionalWeakTable<SilType, AggregateLayout> Layouts = new();

    /// <summary>The bytes of a pointer on the target: 4 on WebAssembly, 8 elsewhere (<see cref="TypeLayout.PointerBytes" />).</summary>
    static int PointerBytes => TypeLayout.PointerBytes;

    /// <summary>A struct's layout: its parts, a live byte first for one with a deinit.</summary>
    static AggregateLayout LayoutOf(StructType type) =>
        Layouts.GetValue(type, static t => Written([..StructParts((StructType)t)], TypeLayout.Of((StructType)t)));

    static AggregateLayout LayoutOf(TupleType type) =>
        Layouts.GetValue(type, static t => Written([..((TupleType)t).Elements], TypeLayout.Of((TupleType)t)));

    /// <summary>An object's layout: the header — its type, its count, its flags — then its fields.</summary>
    static AggregateLayout ObjectLayout(ClassType type) =>
        ObjectLayouts.GetValue(type, static t => Written(TypeLayout.ObjectParts(t), TypeLayout.Object(t)));

    static readonly ConditionalWeakTable<ClassType, AggregateLayout> ObjectLayouts = new();

    /// <summary>
    ///     The alignment LLVM gives the type it writes for this one: less than
    ///     <see cref="Align" /> for an aggregate written as a packed LLVM
    ///     struct, which LLVM aligns to a byte.
    /// </summary>
    static int LlvmAlign(SilType type) => TypeLayout.WrittenAlign(type);

    /// <summary>
    ///     An aggregate as LLVM is given it: a literal struct of its parts,
    ///     when LLVM lays it out as <see cref="TypeLayout" /> does; otherwise a
    ///     packed one, with the padding written out as byte arrays.
    /// </summary>
    static AggregateLayout Written(List<SilType> parts, TypeLayout.Aggregate layout) {
        var slots = new int[parts.Count];
        if (!layout.Explicit) {
            for (var i = 0; i < parts.Count; i++) {
                slots[i] = i;
            }

            return new($"{{ {string.Join(", ", parts.Select(PartType))} }}", slots, layout.Offsets, layout.Size, layout.Align, false);
        }

        var elements = new List<string>();
        var end = 0;
        for (var i = 0; i < parts.Count; i++) {
            var at = layout.Offsets[i];
            if (at > end) {
                elements.Add($"[{at - end} x i8]");
            }

            slots[i] = elements.Count;
            elements.Add(PartType(parts[i]));
            end = at + Size(parts[i]);
        }

        if (layout.Size > end) {
            elements.Add($"[{layout.Size - end} x i8]");
        }

        return new($"<{{ {string.Join(", ", elements)} }}>", slots, layout.Offsets, layout.Size, layout.Align, true);
    }

    /// <summary>How the runtime aligns every block it allocates.</summary>
    const int HeapAlign = TypeLayout.HeapAlign;

    static int RoundUp(int offset, int align) => TypeLayout.RoundUp(offset, align);

    /// <summary>The largest power of two an offset is a multiple of, up to a limit; the limit for 0.</summary>
    static int AlignmentAt(int offset, int limit) => offset == 0 ? limit : Math.Min(limit, offset & -offset);

    /// <summary>
    ///     How aligned the storage an address points to is known to be: its
    ///     type's alignment, unless it is a part of a packed struct, which may
    ///     be at any offset.
    /// </summary>
    static int AddressAlign(SilValue address) =>
        address switch {
            StructElementAddrInst { Address.Type: AddressType { Pointee: StructType { IsInline: false } structType } } element =>
                AlignmentAt(LayoutOf(structType).Offsets[PartIndex(structType, element.Field)], AddressAlign(element.Address)),
            RefElementAddrInst { Reference.Type: ClassType classType } element =>
                AlignmentAt(ObjectLayout(classType).Offsets[element.Field + 3], HeapAlign),
            TupleElementAddrInst { Address.Type: AddressType { Pointee: TupleType tuple } } element =>
                AlignmentAt(LayoutOf(tuple).Offsets[element.Element], AddressAlign(element.Address)),
            EnumPayloadAddrInst { Address.Type: AddressType { Pointee: EnumType enumType } } payload when !SilType.IsIndirect(enumType) =>
                AlignmentAt(TypeLayout.PayloadOffset(enumType), AddressAlign(payload.Address)),
            { Type: AddressType { Pointee: var pointee } } => Align(pointee),
            _ => 1,
        };

    /// <summary>
    ///     What an access of a value of the type through the address says of
    ///     its alignment: nothing, when LLVM's assumption for the type holds;
    ///     otherwise the alignment the address is known to have.
    /// </summary>
    static string AccessAlign(SilValue address, SilType type) {
        // A vector wider than a heap block's alignment may be in one (an
        // array's element, an object's field) at no more than that: said
        // so, so that the target uses loads that do not need more.
        var known = type is VectorType ? Math.Min(AddressAlign(address), HeapAlign) : AddressAlign(address);
        return known < LlvmAlign(type) ? $", align {known}" : "";
    }

    /// <summary>The alignment storage for a value of the type is given, when LLVM would give it less.</summary>
    static string StorageAlign(SilType type) => LlvmAlign(type) < Align(type) ? $", align {Align(type)}" : "";
}
