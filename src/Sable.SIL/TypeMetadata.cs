using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Sable.SIL;

/// <summary>What a type's metadata says it is: the runtime's <c>SABLE_KIND_*</c>.</summary>
public enum MetadataKind {
    Class = 1,
    Struct = 2,
    Enum = 3,
    Union = 4,
    Optional = 5,
    Tuple = 6,
    Function = 7,
    Array = 8,
    Slice = 9,
    Existential = 10,

    /// <summary>A type as a value: <c>Type</c>, and every <c>T.Type</c>.</summary>
    Metatype = 11,

    /// <summary>A number, a Bool, a Char, <c>()</c>.</summary>
    Scalar = 12,

    String = 13,

    /// <summary>A weak or unowned reference, as a field holds it.</summary>
    Reference = 14,

    /// <summary>What the runtime holds without describing: a closure's context, a region, a view of inline bytes.</summary>
    Opaque = 15,
}

/// <summary>What a type's metadata says of its values: the runtime's <c>SABLE_TYPE_*</c> flags.</summary>
[Flags]
public enum MetadataFlags {
    None = 0,

    /// <summary>Copied by copying its bits, destroyed by doing nothing.</summary>
    Trivial = 1,

    /// <summary>Move-only: its witnesses copy nothing (their copy slots are null).</summary>
    Noncopyable = 2,

    /// <summary>Destroying one runs a <c>deinit</c>.</summary>
    HasDeinit = 4,

    /// <summary>The metadata carries field descriptors (<see cref="SilModule.FieldDescriptors" />).</summary>
    Fields = 8,

    /// <summary>Its field descriptors name their fields' types' metadata.</summary>
    FieldTypes = 16,

    /// <summary>
    ///     One counted reference, never null — an object, a string: an optional
    ///     of it is the reference, null for none, as unspecialised code lays one
    ///     out at run time (#297) as TypeLayout does.
    /// </summary>
    NullablePayload = 32,

    /// <summary>A Bool: an enum of it and cases without payloads takes its spare values, 2 on.</summary>
    BoolNiche = 64,

    /// <summary>A structural union, <c>A | B</c>, whose arguments are its cases' types, in the order of their names.</summary>
    StructuralUnion = 256,

    /// <summary>A Char: an enum of it and cases without payloads takes the values past U+10FFFF.</summary>
    CharNiche = 128,
}

/// <summary>How a type's values are copied and destroyed, as its value-witness table does it.</summary>
public enum WitnessKind {
    /// <summary>The runtime's table for trivial values: copies the bits.</summary>
    Bits,

    /// <summary>The runtime's table for one counted reference, possibly null: retains and releases it.</summary>
    Reference,

    /// <summary>A table the compiler writes for the type: copies and destroys each reference it holds.</summary>
    Emitted,
}

/// <summary>
///     One field descriptor (docs/06 §6.2, <c>SableField</c>): a stored field
///     of a struct, a class or a tuple, or a case of an enum with where its
///     payload is.
/// </summary>
/// <param name="Name">The field's name; a tuple's element's index; a case's name.</param>
/// <param name="TypeName">Its type as Sable writes it; empty for a case with no payload.</param>
/// <param name="Offset">Where it is from the start of the value — of an object, for a class, the header's bytes among them.</param>
/// <param name="Kind">What it holds: <c>SABLE_FIELD_*</c>, 0 data, 1 strong, 2 aggregate, 3 weak or unowned.</param>
/// <param name="Type">Its type, whose metadata the descriptor points at; null for a case with no payload.</param>
public sealed record MetadataField(string Name, string TypeName, int Offset, int Kind, SilType? Type);

/// <summary>
///     A type's metadata (docs/06 §6.2), as the code generator writes it and
///     the interpreter models it: what kind of type it is, its layout, how its
///     values are copied and destroyed, its fields, its type arguments and its
///     name. Both build it here, so they agree.
/// </summary>
/// <remarks>
///     <para>
///         Natively, a type's metadata is a constant, <c>SableTypeMetadata</c>,
///         one per type in the program: <c>linkonce_odr</c> in a COMDAT when
///         the program is built in units, so that the linker keeps one and its
///         address is the type's identity. It is emitted for the types the
///         program asks about (<c>metadata $T</c>), the classes it makes, and
///         the types an existential's box holds, with their conformance
///         records (<see cref="SilModule.ConformancesOf" />) and what those
///         reach.
///     </para>
///     <para>
///         A generic type's instance carries its type arguments: today each is
///         emitted for every instance the program uses, and the arguments are
///         what metadata instantiated at run time will be cached by.
///     </para>
/// </remarks>
public sealed class TypeMetadata {
    TypeMetadata(SilType type) => Type = type;

    public SilType Type { get; }
    public MetadataKind Kind { get; private init; }
    public string Name { get; private init; } = "";
    public int Size { get; private init; }
    public int Alignment { get; private init; }
    public int Stride { get; private init; }
    public MetadataFlags Flags { get; private init; }
    public WitnessKind Witnesses { get; private init; }

    /// <summary>Every field descriptor the type has, whether or not its metadata carries them (<see cref="FieldsIn" />).</summary>
    public ImmutableArray<MetadataField> AllFields { get; private init; } = [];

    /// <summary>
    ///     Its type arguments: a generic type's; an optional's wrapped type,
    ///     an array's or a slice's element, a tuple's elements, a function's
    ///     parameters and then its result, a weak or unowned reference's
    ///     referent, an existential's box's root class.
    /// </summary>
    public ImmutableArray<SilType> Arguments { get; private init; } = [];

    /// <summary>The field descriptors the type's metadata carries in this module: a class's always, every type's when the module asks.</summary>
    public ImmutableArray<MetadataField> FieldsIn(SilModule? module) =>
        module?.FieldDescriptors == true || Kind == MetadataKind.Class ? AllFields : [];

    /// <summary>The flags of the type's metadata in this module: whether it carries field descriptors, and their types.</summary>
    public MetadataFlags FlagsIn(SilModule? module) =>
        Flags | (FieldsIn(module).IsEmpty ? MetadataFlags.None : MetadataFlags.Fields)
              | (module?.FieldDescriptors == true ? MetadataFlags.FieldTypes : MetadataFlags.None);

    // By the pointer width a layout depends on: a program for WebAssembly may be compiled beside a native one.
    static readonly ConditionalWeakTable<SilType, TypeMetadata> Cache = new(), Cache32 = new();

    /// <summary>The type's metadata, described for the target being compiled for (<see cref="TypeLayout.PointerBytes" />).</summary>
    public static TypeMetadata Of(SilType type) => (TypeLayout.PointerBytes == 4 ? Cache32 : Cache).GetValue(type, Describe);

    static TypeMetadata Describe(SilType type) {
        var kind = KindOf(type);
        var noncopyable = type is NominalType { IsNoncopyable: true };
        var deinit = type is StructType { Deinit: not null } || type is ClassType classType && Deinits(classType);
        var flags = (type.IsTrivial && !noncopyable ? MetadataFlags.Trivial : MetadataFlags.None)
                    | (noncopyable ? MetadataFlags.Noncopyable : MetadataFlags.None)
                    | (deinit ? MetadataFlags.HasDeinit : MetadataFlags.None)
                    | (IsStructuralUnion(type) ? MetadataFlags.StructuralUnion : MetadataFlags.None)
                    | (type is ClassType || type == SilType.String ? MetadataFlags.NullablePayload : MetadataFlags.None)
                    | (type == SilType.Bool ? MetadataFlags.BoolNiche : MetadataFlags.None)
                    | (type == SilType.Char ? MetadataFlags.CharNiche : MetadataFlags.None);
        return new TypeMetadata(type) {
            Kind = kind,
            Name = DisplayName(type),
            Size = TypeLayout.Size(type),
            Alignment = TypeLayout.Align(type),
            Stride = TypeLayout.Stride(type),
            Flags = flags,
            Witnesses = WitnessesOf(type),
            AllFields = [..FieldsOf(type)],
            Arguments = ArgumentsOf(type),
        };
    }

    static bool Deinits(ClassType type) {
        for (var level = type; level is not null; level = level.Superclass) {
            if (level.Deinit is not null) {
                return true;
            }
        }

        return false;
    }

    /// <summary>True for what <c>any P</c> lowers to: the box, the metadata, and a witness table per protocol.</summary>
    public static bool IsExistential(SilType type) => type is StructType { Name: var name } && name.StartsWith("any$", StringComparison.Ordinal);

    /// <summary>True for an optional: <c>Optional$…</c>, some and none.</summary>
    public static bool IsOptional(SilType type) =>
        type is EnumType { Name: var name, Cases: [("Some", _), ("None", null)] } && name.StartsWith("Optional$", StringComparison.Ordinal);

    /// <summary>True for a structural union, <c>A | B</c> (SilGen.LowerUnion).</summary>
    public static bool IsStructuralUnion(SilType type) => type is EnumType { IsUnion: true, Name: var name } && name.StartsWith("Union$", StringComparison.Ordinal);

    public static MetadataKind KindOf(SilType type) =>
        type switch {
            _ when type == SilType.String => MetadataKind.String,
            _ when type == SilType.Metadata => MetadataKind.Metatype,
            _ when type == SilType.Object || type == SilType.Region || type == SilType.WitnessTable => MetadataKind.Opaque,
            OpaqueType => MetadataKind.Opaque,
            ScalarType => MetadataKind.Scalar,
            ClassType => MetadataKind.Class,
            StructType when IsExistential(type) => MetadataKind.Existential,
            StructType => MetadataKind.Struct,
            EnumType when IsOptional(type) => MetadataKind.Optional,
            EnumType { IsUnion: true } => MetadataKind.Union,
            EnumType => MetadataKind.Enum,
            TupleType => MetadataKind.Tuple,
            FunctionType or ThickFunctionType => MetadataKind.Function,
            ArrayType => MetadataKind.Array,
            SliceType => MetadataKind.Slice,
            WeakStorageType or UnownedStorageType => MetadataKind.Reference,
            _ => MetadataKind.Opaque,
        };

    /// <summary>
    ///     How a value of the type is copied and destroyed: as bits; as one
    ///     counted reference — a class's, a string's, an array's, a region's,
    ///     an inline struct's buffer, an optional reference; or by a table of
    ///     the type's own.
    /// </summary>
    public static WitnessKind WitnessesOf(SilType type) =>
        type.IsTrivial && type is not NominalType { IsNoncopyable: true } ? WitnessKind.Bits
        : type is ScalarType or ClassType or ArrayType or StructType { IsInline: true } || type is EnumType nullable && TypeLayout.IsNullable(nullable)
            ? WitnessKind.Reference
            : WitnessKind.Emitted;

    static IEnumerable<MetadataField> FieldsOf(SilType type) {
        switch (type) {
            case StructType { IsInline: false } structType:
                for (var i = 0; i < structType.Fields.Count; i++) {
                    yield return Field(structType.Fields[i].Name, structType.Fields[i].Type, TypeLayout.FieldOffset(structType, i));
                }

                break;

            case ClassType classType:
                for (var i = 0; i < classType.Fields.Count; i++) {
                    yield return Field(classType.Fields[i].Name, classType.Fields[i].Type, TypeLayout.FieldOffset(classType, i));
                }

                break;

            case TupleType tuple: {
                var layout = TypeLayout.Of(tuple);
                for (var i = 0; i < tuple.Elements.Length; i++) {
                    yield return Field(i.ToString(System.Globalization.CultureInfo.InvariantCulture), tuple.Elements[i], layout.Offsets[i]);
                }

                break;
            }

            // A case: its name, and its payload where every case's is.
            case EnumType enumType: {
                var offset = TypeLayout.PayloadOffset(enumType);
                foreach (var (name, payload) in enumType.Cases) {
                    yield return payload is null ? new MetadataField(name, "", offset, 0, null) : Field(name, payload, offset);
                }

                break;
            }
        }

        static MetadataField Field(string name, SilType type, int offset) => new(name, DisplayName(type), offset, FieldKind(type), type);
    }

    internal static ImmutableArray<SilType> ArgumentsOf(SilType type) =>
        type switch {
            NominalType { TypeArguments.IsEmpty: false } generic => generic.TypeArguments,
            EnumType optional when IsOptional(optional) && optional.Cases[0].Payload is { } wrapped => [wrapped],
            EnumType union when IsStructuralUnion(union) => [..union.Cases.Select(static c => c.Payload!)],
            ArrayType array => [array.Element],
            SliceType slice => [slice.Element],
            TupleType tuple => tuple.Elements,
            FunctionType function => [..function.Parameters, function.Result],
            ThickFunctionType thick => [..thick.Signature.Parameters, thick.Signature.Result],
            WeakStorageType weak => [weak.Referent],
            UnownedStorageType unowned => [unowned.Referent],
            _ => [],
        };

    /// <summary>What a field holds, as the runtime's <c>SABLE_FIELD_*</c> says: what the leak report follows.</summary>
    public static int FieldKind(SilType type) =>
        type switch {
            WeakStorageType or UnownedStorageType => 3,
            ClassType or ArrayType => 1,
            EnumType enumType when TypeLayout.IsNullable(enumType) => 1,
            _ when type == SilType.String || type == SilType.Object => 1,
            _ when type.IsTrivial => 0,
            _ => 2,
        };

    /// <summary>A type's name as Sable writes it, as the debugger shows it: <c>Zoo.Animal?</c>, <c>Sable.Collections.Deque&lt;String&gt;</c>.</summary>
    public static string DisplayName(SilType type) =>
        type switch {
            ArrayType array => $"[{DisplayName(array.Element)}]",
            SliceType slice => $"[{DisplayName(slice.Element)}:]",
            EnumType enumType when OptionalPayload(enumType) is { } payload => DisplayName(payload) + "?",
            StructType { DisplayName: { } existential, Name: var name } when name.StartsWith("any$", StringComparison.Ordinal) => existential,
            StructType { Name: var name } when name.StartsWith("any$", StringComparison.Ordinal) => "any " + name[4..].Replace("$and$", " & ", StringComparison.Ordinal),
            TupleType tuple => $"({string.Join(", ", tuple.Elements.Select(DisplayName))})",
            ThickFunctionType thick => FunctionName(thick.Signature),
            FunctionType function => FunctionName(function),
            AddressType address => "inout " + DisplayName(address.Pointee),
            WeakStorageType weak => "weak " + DisplayName(weak.Optional),
            UnownedStorageType unowned => "unowned " + DisplayName(unowned.Referent),
            NominalType { DisplayName: { } written } => written,
            _ when type == SilType.Metadata => "Type",
            _ => type.ToString(),
        };

    static string FunctionName(FunctionType function) =>
        $"({string.Join(", ", function.Parameters.Select(DisplayName))}){(function.Thrown is { } thrown ? $" throws({DisplayName(thrown)})" : "")} -> {DisplayName(function.Result)}";

    /// <summary>The wrapped type, for an optional.</summary>
    public static SilType? OptionalPayload(EnumType type) =>
        type.Name.StartsWith("Optional$", StringComparison.Ordinal) && type.Cases is [("Some", { } payload), ("None", null)] ? payload : null;
}

/// <summary>
///     A conformance's witness table (docs/06 §6.2, #299): one entry per
///     requirement the protocol itself declares, in declaration order, each
///     the function that meets it for the conforming type in the generic
///     calling convention — what unspecialised code and a call through an
///     existential both call; then the tables of the protocols it refines,
///     and the type's associated types with what each must conform to.
/// </summary>
/// <remarks>
///     Natively a constant the conformer's metadata's conformance record
///     points at, so that <c>sable_conformance</c> finds it from any value's
///     metadata: <c>e as? any Q</c> and <c>e is any Q</c> are lookups in that
///     registry. An existential holds a pointer to it for each of its
///     protocols.
/// </remarks>
public sealed class SilWitnessTable(SilType conformer, string protocol) {
    /// <summary>The conforming type.</summary>
    public SilType Conformer { get; } = conformer;

    /// <summary>The protocol, by the name its descriptor has: <c>M.Shape</c>.</summary>
    public string Protocol { get; } = protocol;

    /// <summary>
    ///     A generic type's table for every instance of it made at run time
    ///     (SilGen.Templates.cs): its conformer is the instance with the type's
    ///     own parameters opaque, and its entries read the type arguments from
    ///     Self's metadata. Registered by the type's name.
    /// </summary>
    public bool IsTemplate { get; init; }

    /// <summary>
    ///     A template's functions that give each associated type's metadata
    ///     from Self's, in the order of <see cref="AssociatedTypes" />: an
    ///     instance made at run time has a table of its own, with them asked.
    ///     Empty when it has no associated type and refines no protocol that has.
    /// </summary>
    public List<string> Accessors { get; } = [];

    /// <summary>
    ///     Each requirement the protocol itself declares, in declaration order,
    ///     with its witness in the generic calling convention (#297, #299):
    ///     <c>self</c> (its storage's address, for one that changes it) unless
    ///     it is static, its parameters, where a result of <c>Self</c> goes,
    ///     then <c>Self</c>'s metadata. A value of <c>Self</c> is an
    ///     <see cref="OpaqueType" />, borrowed where its caller holds it. Null
    ///     where no witness is made: a requirement that convention cannot pass.
    /// </summary>
    public List<(string Requirement, string? Function, FunctionType? Type)> Entries { get; } = [];

    /// <summary>
    ///     The protocols the protocol refines directly, in the order it names
    ///     them (#297): natively the conformer's table for each follows, so
    ///     that unspecialised code given this table reaches a refined
    ///     protocol's (<c>witness_base:i</c>) without asking the registry.
    ///     Made in an unspecialised build.
    /// </summary>
    public List<string> Bases { get; } = [];

    /// <summary>Each associated type the protocol has, by name, as the conformance binds it.</summary>
    public List<(string Name, SilType Type)> AssociatedTypes { get; } = [];

    /// <summary>
    ///     What each associated type must conform to, by its index among
    ///     <see cref="AssociatedTypes" /> and the protocol's name: found in the
    ///     registry from the associated type's metadata.
    /// </summary>
    public List<(int AssociatedType, string Protocol)> AssociatedConformances { get; } = [];
}
