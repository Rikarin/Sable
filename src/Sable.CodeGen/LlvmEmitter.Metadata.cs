using System.Globalization;
using System.Text;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     Type metadata, value-witness tables, witness tables and protocol
///     descriptors (docs/06 §6.2) as constants: what <see cref="TypeMetadata" />
///     describes, written the first time something refers to it.
/// </summary>
/// <remarks>
///     <para>
///         Each is linked as <see cref="SharedLinkage" /> says: one unit of a
///         program compiled in several writes its own copy of what it uses,
///         <c>linkonce_odr</c> (in a COMDAT on Windows), and the linker keeps
///         one, so that a type's metadata has one address in the program.
///     </para>
///     <para>
///         A type's metadata is written for the types the program asks about
///         (<c>metadata $T</c>), each class it makes or tests, each type an
///         existential's box holds — a box class's one type argument — and
///         what those reach: field types when the module carries field
///         descriptors' types, type arguments, and witness tables.
///     </para>
/// </remarks>
public sealed partial class LlvmEmitter {
    /// <summary>The runtime's <c>SableTypeMetadata</c>.</summary>
    const string TypeMetadataRecord = "{ i64, i64, ptr, i64, ptr, ptr, i64, i64, i64, i64, ptr, i64, ptr, ptr }";

    /// <summary>Where each field of <c>SableTypeMetadata</c> is.</summary>
    internal const int MetadataKindField = 0, MetadataFieldCountField = 1, MetadataNameField = 5, MetadataSizeField = 6, MetadataAlignmentField = 7,
        MetadataStrideField = 8, MetadataFlagsField = 9, MetadataWitnessesField = 10, MetadataArgumentCountField = 11;

    /// <summary>The runtime's <c>SableMetadata</c> up to its vtable, and where its type's metadata is in it.</summary>
    const string ObjectMetadataHead = "{ ptr, ptr, i64, ptr, ptr, i64, i64, ptr, ptr }";

    const int ObjectMetadataTypeField = 8;

    /// <summary>Where each witness is in a value-witness table, <c>SableValueWitnesses</c>.</summary>
    internal const int CopyWitness = 0, TakeWitness = 1, AssignWitness = 2, DestroyWitness = 4;

    /// <summary>The runtime's <c>SableField</c>: name, type name, offset, kind, type.</summary>
    const string FieldDescriptorType = "{ ptr, ptr, i64, i64, ptr }";

    /// <summary>The runtime's <c>SableWitnessTable</c>.</summary>
    const string WitnessTableRecord = "{ ptr, i64, ptr, i64, ptr, i64, ptr }";

    /// <summary>
    ///     A type's metadata, <c>@"sable.type.T"</c>: its kind, its fields, its
    ///     conformance records, its name, its layout, its flags, its value
    ///     witnesses, its type arguments and, for a class, its object metadata.
    /// </summary>
    internal string TypeMetadataGlobal(SilType type) {
        var name = $"@\"sable.type.{Mangled(type)}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        // Everything it refers to is written first, so that no other global lands inside this one.
        var metadata = TypeMetadata.Of(type);
        var fieldTypes = _module?.FieldDescriptors == true;
        var denied = _module?.DenyReflection == true;
        var fields = metadata.FieldsIn(_module);
        var fieldArray = "null";
        if (!fields.IsEmpty) {
            fieldArray = $"@\"sable.fields.{Mangled(type)}\"";
            var descriptors = fields.Select((f, i) =>
                $"{FieldDescriptorType} {{ {(denied ? "ptr null, ptr null" : $"ptr {TrapMessage(f.Name)}, ptr {TrapMessage(f.TypeName)}")}, "
                + $"i64 {LlvmOffset(type, i, f)}, i64 {f.Kind}, ptr {(fieldTypes && f.Type is { } fieldType ? TypeMetadataGlobal(fieldType) : "null")} }}").ToList();
            _globals.Append($"{fieldArray} = {SharedLinkage} constant [{fields.Length} x {FieldDescriptorType}] [{string.Join(", ", descriptors)}]{Comdat(fieldArray, SharedLinkage)}\n");
        }

        var records = (_module?.ConformancesOf(type) ?? []).Select(r => $"{{ ptr, ptr }} {{ ptr {ProtocolDescriptor(r.Protocol)}, ptr {WitnessesOf(r.Witnesses)} }}").ToList();
        var conformances = "null";
        if (records.Count > 0) {
            conformances = $"@\"sable.conformances.{Mangled(type)}\"";
            _globals.Append($"{conformances} = {SharedLinkage} constant [{records.Count} x {{ ptr, ptr }}] [{string.Join(", ", records)}]{Comdat(conformances, SharedLinkage)}\n");
        }

        var argumentList = metadata.Arguments.Select(a => $"ptr {TypeMetadataGlobal(a)}").ToList();
        var arguments = "null";
        if (argumentList.Count > 0) {
            arguments = $"@\"sable.arguments.{Mangled(type)}\"";
            _globals.Append($"{arguments} = {SharedLinkage} constant [{argumentList.Count} x ptr] [{string.Join(", ", argumentList)}]{Comdat(arguments, SharedLinkage)}\n");
        }

        var witnesses = ValueWitnessTable(type, metadata);
        var typeName = TrapMessage(metadata.Name);
        var objectMetadata = type is ClassType classType ? ClassMetadata(classType) : "null";
        var flags = (int)metadata.FlagsIn(_module);
        _globals.Append($"{name} = {SharedLinkage} constant %SableTypeMetadata {{ i64 {(int)metadata.Kind}, i64 {fields.Length}, ptr {fieldArray}, ")
            .Append($"i64 {records.Count}, ptr {conformances}, ptr {typeName}, i64 {LlvmSize(type, metadata)}, i64 {LlvmAlignment(type, metadata)}, i64 {LlvmStride(type, metadata)}, ")
            .Append($"i64 {flags}, ptr {witnesses}, i64 {argumentList.Count}, ptr {arguments}, ptr {objectMetadata} }}{Comdat(name, SharedLinkage)}\n");
        return name;
    }

    // A type's layout, as LLVM computes it from the type it is given: so that
    // a native program's metadata says what the code it runs does, and a test
    // that it agrees with the interpreter's (TypeLayout's) checks the two.

    /// <summary>Whether LLVM has a type for values of this one to measure; else TypeLayout's numbers are written.</summary>
    static bool Measurable(SilType type) =>
        type != SilType.Unit && type is ScalarType or ClassType or ArrayType or SliceType or AddressType or FunctionType or ThickFunctionType or TupleType
            or WeakStorageType or UnownedStorageType or StructType or EnumType;

    static string LlvmSize(SilType type, TypeMetadata metadata) => Measurable(type) ? SizeOf(Type(type)) : metadata.Size.ToString(CultureInfo.InvariantCulture);

    /// <summary>The stride: what an array of two of them puts between them, as LLVM lays it out.</summary>
    static string LlvmStride(SilType type, TypeMetadata metadata) =>
        Measurable(type) ? $"ptrtoint (ptr getelementptr ({Type(type)}, ptr null, i32 1) to i64)" : metadata.Stride.ToString(CultureInfo.InvariantCulture);

    /// <summary>The alignment: LLVM's, unless the aggregate is written packed, padding and all, when only the layout knows it.</summary>
    static string LlvmAlignment(SilType type, TypeMetadata metadata) =>
        Measurable(type) && LlvmAlign(type) == Align(type) ? AlignmentOf(Type(type)) : metadata.Alignment.ToString(CultureInfo.InvariantCulture);

    /// <summary>Where a field is, as LLVM lays out the type: its part's element in the LLVM type.</summary>
    static string LlvmOffset(SilType type, int field, MetadataField descriptor) =>
        type switch {
            StructType { IsInline: false } structType => $"ptrtoint (ptr getelementptr ({LayoutOf(structType).Llvm}, ptr null, i32 0, i32 {FieldSlot(structType, field)}) to i64)",
            TupleType tuple => $"ptrtoint (ptr getelementptr ({LayoutOf(tuple).Llvm}, ptr null, i32 0, i32 {LayoutOf(tuple).Slots[field]}) to i64)",
            ClassType classType => $"ptrtoint (ptr getelementptr ({ObjectType(classType)}, ptr null, i32 0, i32 {ObjectSlot(classType, field)}) to i64)",
            EnumType enumType when descriptor.Type is not null && !IsNullable(enumType) && Niche(enumType) is null && Area(enumType).Count > 0
                => $"ptrtoint (ptr getelementptr ({Type(enumType)}, ptr null, i32 0, i32 1) to i64)",
            _ => descriptor.Offset.ToString(CultureInfo.InvariantCulture),
        };

    /// <summary>
    ///     A type's value witnesses: the runtime's for a trivial value and for
    ///     one counted reference; for anything else a table of the type's own
    ///     — its copy, destroy and visit over memory, around the value witness
    ///     functions <see cref="Witness" /> writes, and the runtime's take and
    ///     assignments, which work from those through the metadata.
    /// </summary>
    string ValueWitnessTable(SilType type, TypeMetadata metadata) {
        if (metadata.Witnesses == WitnessKind.Bits) {
            return "@sable_bits_witnesses";
        }

        if (metadata.Witnesses == WitnessKind.Reference) {
            return "@sable_reference_witnesses";
        }

        var name = $"@\"sable.vwt.{Mangled(type)}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        var llvmType = Type(type);
        var noncopyable = metadata.Flags.HasFlag(MetadataFlags.Noncopyable);
        var copy = $"@\"sable.vw.copy.{Mangled(type)}\"";
        var destroy = $"@\"sable.vw.destroy.{Mangled(type)}\"";
        var visit = $"@\"sable.vw.visit.{Mangled(type)}\"";
        var body = new StringBuilder();
        if (!noncopyable) {
            body.Append($"define internal void {copy}(ptr %d, ptr %s, ptr %t) {{\nentry:\n  %v = load {llvmType}, ptr %s{StorageAlign(type)}\n");
            Ownership(body, "  ", true, type, "%v");
            body.Append($"  store {llvmType} %v, ptr %d{StorageAlign(type)}\n  ret void\n}}\n\n");
        }

        body.Append($"define internal void {destroy}(ptr %e, ptr %t) {{\nentry:\n  %v = load {llvmType}, ptr %e{StorageAlign(type)}\n");
        Ownership(body, "  ", false, type, "%v");
        body.Append("  ret void\n}\n\n");
        body.Append($"define internal void {visit}(ptr %e, ptr %t) {{\nentry:\n  %v = load {llvmType}, ptr %e{StorageAlign(type)}\n");
        Ownership(body, "  ", Witnessing.Visit, type, "%v");
        body.Append("  ret void\n}\n\n");
        _helpers.Append(body);

        var copies = noncopyable ? "ptr null, ptr @sable_vw_take, ptr null" : $"ptr {copy}, ptr @sable_vw_take, ptr @sable_vw_assign_with_copy";
        _globals.Append($"{name} = {SharedLinkage} constant {{ ptr, ptr, ptr, ptr, ptr, ptr }} {{ {copies}, ptr @sable_vw_assign_with_take, ptr {destroy}, ptr {visit} }}{Comdat(name, SharedLinkage)}\n");
        return name;
    }

    /// <summary>The name of a conformance's witness table.</summary>
    static string WitnessTableName(SilType conformer, string protocol) =>
        $"@\"sable.witnesses.{Mangled(conformer)}.{protocol.Replace("\"", "", StringComparison.Ordinal)}\"";

    /// <summary>
    ///     What a conformance record's witnesses are: the table, where this
    ///     module made one. A unit of several leaves it null whether it made
    ///     the table or not, and registers the tables it made as the program
    ///     starts (<see cref="WitnessRegistration" />): which tables a unit
    ///     makes depends on what its own code boxes and casts, and every
    ///     unit's copy of a type's records must be the same, as
    ///     <c>linkonce_odr</c> requires.
    /// </summary>
    string WitnessesOf(SilWitnessTable? table) => table is not null && _module?.Unit is null ? WitnessTableGlobal(table) : "null";

    /// <summary>
    ///     In one unit of several, a constructor that hands the runtime each
    ///     witness table the unit made, with its conformer's metadata
    ///     (<c>sable_register_witness_table</c>): where the registry finds the
    ///     tables a record leaves null. Null when there are none.
    /// </summary>
    string? WitnessRegistration(SilModule module) {
        if (module.Unit is null || module.WitnessTables.Count == 0) {
            return null;
        }

        var body = new StringBuilder("define internal void @\"sable.witnesses.register\"() {\nentry:\n");
        foreach (var table in module.WitnessTables) {
            body.Append($"  call void @sable_register_witness_table(ptr {TypeMetadataGlobal(table.Conformer)}, ptr {WitnessTableGlobal(table)})\n");
        }

        _helpers.Append(body.Append("  ret void\n}\n\n"));
        return "@\"sable.witnesses.register\"";
    }

    /// <summary>
    ///     A conformance's witness table, <c>@"sable.witnesses.T.P"</c>: the
    ///     protocol, its requirements' witnesses in order, its associated
    ///     types' metadata and what each must conform to. A witness no
    ///     reachable code can need was not built: its slot is empty.
    /// </summary>
    string WitnessTableGlobal(SilWitnessTable table) {
        var name = WitnessTableName(table.Conformer, table.Protocol);
        if (!_emitted.Add(name)) {
            return name;
        }

        var protocol = ProtocolDescriptor(table.Protocol);
        var requirements = "null";
        if (table.Entries.Count > 0) {
            requirements = name[..^1] + ".requirements\"";
            var entries = table.Entries.Select(e => _reachable?.Contains(e.Function) == false || _module?.Find(e.Function) is null && _module?.Unit is null ? "ptr null" : $"ptr @{Global(e.Function)}");
            _globals.Append($"{requirements} = {SharedLinkage} constant [{table.Entries.Count} x ptr] [{string.Join(", ", entries)}]{Comdat(requirements, SharedLinkage)}\n");
        }

        var associatedTypes = "null";
        if (table.AssociatedTypes.Count > 0) {
            associatedTypes = name[..^1] + ".associated\"";
            var types = table.AssociatedTypes.Select(a => $"ptr {TypeMetadataGlobal(a.Type)}").ToList();
            _globals.Append($"{associatedTypes} = {SharedLinkage} constant [{types.Count} x ptr] [{string.Join(", ", types)}]{Comdat(associatedTypes, SharedLinkage)}\n");
        }

        var associatedConformances = "null";
        if (table.AssociatedConformances.Count > 0) {
            associatedConformances = name[..^1] + ".associated_conformances\"";
            var records = table.AssociatedConformances.Select(c => $"{{ i64, ptr }} {{ i64 {c.AssociatedType}, ptr {ProtocolDescriptor(c.Protocol)} }}").ToList();
            _globals.Append($"{associatedConformances} = {SharedLinkage} constant [{records.Count} x {{ i64, ptr }}] [{string.Join(", ", records)}]{Comdat(associatedConformances, SharedLinkage)}\n");
        }

        _globals.Append($"{name} = {SharedLinkage} constant {WitnessTableRecord} {{ ptr {protocol}, i64 {table.Entries.Count}, ptr {requirements}, ")
            .Append($"i64 {table.AssociatedTypes.Count}, ptr {associatedTypes}, i64 {table.AssociatedConformances.Count}, ptr {associatedConformances} }}{Comdat(name, SharedLinkage)}\n");
        return name;
    }

    /// <summary>
    ///     A protocol's descriptor, <c>SableProtocol</c>: its name. Every unit
    ///     that needs one makes it, and the linker keeps one, so it is the
    ///     protocol's identity to <c>sable_conformance</c>; across images,
    ///     which each have their own, the name is.
    /// </summary>
    internal string ProtocolDescriptor(string protocol) {
        var name = $"@\"sable.protocol.{protocol.Replace("\"", "", StringComparison.Ordinal)}\"";
        if (_emitted.Add(name)) {
            var text = TrapMessage(protocol);
            _globals.Append($"{name} = {SharedLinkage} constant {{ ptr }} {{ ptr {text} }}{Comdat(name, SharedLinkage)}\n");
        }

        return name;
    }

    const string MetadataDeclarations = """
                                        %SableValueWitnesses = type { ptr, ptr, ptr, ptr, ptr, ptr }
                                        @sable_bits_witnesses = external constant %SableValueWitnesses
                                        @sable_reference_witnesses = external constant %SableValueWitnesses
                                        declare void @sable_vw_take(ptr, ptr, ptr)
                                        declare void @sable_vw_assign_with_copy(ptr, ptr, ptr)
                                        declare void @sable_vw_assign_with_take(ptr, ptr, ptr)
                                        declare ptr @sable_metadata_name(ptr)
                                        declare ptr @sable_metadata_field_name(ptr, i64)
                                        declare i64 @sable_metadata_field_offset(ptr, i64)
                                        declare ptr @sable_metadata_field_type(ptr, i64)
                                        declare ptr @sable_metadata_argument(ptr, i64)
                                        declare ptr @sable_box_value_metadata(ptr)
                                        declare i1 @sable_metadata_equal(ptr, ptr)
                                        declare i64 @sable_metadata_hash(ptr)
                                        declare i1 @sable_metadata_is_subtype(ptr, ptr)
                                        declare ptr @sable_witness_table(ptr, ptr)
                                        declare void @sable_register_witness_table(ptr, ptr)

                                        """;

    sealed partial class FunctionEmitter {
        /// <summary>
        ///     The builtins that read a type's metadata, call its value
        ///     witnesses, and look a conformance up in the registry; false for
        ///     any other.
        /// </summary>
        bool MetadataBuiltin(BuiltinInst builtin, string[] operands) {
            string Result() {
                var result = Fresh();
                _names[builtin] = result;
                return result;
            }

            string Field(string metadata, int field) {
                var at = Fresh();
                Line($"{at} = getelementptr inbounds %SableTypeMetadata, ptr {metadata}, i32 0, i32 {field}");
                return at;
            }

            // A witness of the metadata's value-witness table.
            string Witness(string metadata, int witness) {
                var table = Fresh();
                Line($"{table} = load ptr, ptr {Field(metadata, MetadataWitnessesField)}");
                var at = Fresh();
                Line($"{at} = getelementptr inbounds %SableValueWitnesses, ptr {table}, i32 0, i32 {witness}");
                var function = Fresh();
                Line($"{function} = load ptr, ptr {at}");
                return function;
            }

            // A value in memory of its own, for a witness to work on.
            string Slot(SilType type, string? value) {
                var slot = Fresh();
                Line($"{slot} = alloca {Type(type)}{StorageAlign(type)}");
                if (value is not null) {
                    Line($"store {Type(type)} {value}, ptr {slot}{StorageAlign(type)}");
                }

                return slot;
            }

            switch (builtin.Name) {
                case "metadata_kind" or "metadata_size" or "metadata_alignment" or "metadata_stride" or "metadata_flags" or "metadata_field_count" or "metadata_argument_count": {
                    var field = builtin.Name switch {
                        "metadata_kind" => MetadataKindField,
                        "metadata_size" => MetadataSizeField,
                        "metadata_alignment" => MetadataAlignmentField,
                        "metadata_stride" => MetadataStrideField,
                        "metadata_flags" => MetadataFlagsField,
                        "metadata_field_count" => MetadataFieldCountField,
                        _ => MetadataArgumentCountField,
                    };
                    Line($"{Result()} = load i64, ptr {Field(operands[0], field)}");
                    return true;
                }

                case "metadata_name":
                    Line($"{Result()} = call ptr @sable_metadata_name(ptr {operands[0]})");
                    return true;

                case "metadata_field_name":
                    Line($"{Result()} = call ptr @sable_metadata_field_name(ptr {operands[0]}, i64 {operands[1]})");
                    return true;

                case "metadata_field_offset":
                    Line($"{Result()} = call i64 @sable_metadata_field_offset(ptr {operands[0]}, i64 {operands[1]})");
                    return true;

                case "metadata_field_type":
                    Line($"{Result()} = call ptr @sable_metadata_field_type(ptr {operands[0]}, i64 {operands[1]})");
                    return true;

                case "metadata_argument":
                    Line($"{Result()} = call ptr @sable_metadata_argument(ptr {operands[0]}, i64 {operands[1]})");
                    return true;

                // One record per type in an image, so equal addresses are one type; across
                // images (a Sable shared library and the program) the runtime compares names.
                case "metadata_equal":
                    Line($"{Result()} = call i1 @sable_metadata_equal(ptr {operands[0]}, ptr {operands[1]})");
                    return true;

                case "metadata_hash":
                    Line($"{Result()} = call i64 @sable_metadata_hash(ptr {operands[0]})");
                    return true;

                case "metadata_is_subtype":
                    Line($"{Result()} = call i1 @sable_metadata_is_subtype(ptr {operands[0]}, ptr {operands[1]})");
                    return true;

                // An object's own class: its header's object metadata's type metadata.
                case "object_metadata": {
                    var header = Fresh();
                    Line($"{header} = load ptr, ptr {operands[0]}");
                    var at = Fresh();
                    Line($"{at} = getelementptr inbounds {ObjectMetadataHead}, ptr {header}, i32 0, i32 {ObjectMetadataTypeField}");
                    Line($"{Result()} = load ptr, ptr {at}");
                    return true;
                }

                // A copy of the value, made by the metadata's initializeWithCopy (or moved by its initializeWithTake).
                case "value_witness_copy" or "value_witness_take": {
                    var type = builtin.Operands[1].Type;
                    var source = Slot(type, operands[1]);
                    var destination = Slot(type, null);
                    var witness = Witness(operands[0], builtin.Name == "value_witness_copy" ? CopyWitness : TakeWitness);
                    Line($"call void {witness}(ptr {destination}, ptr {source}, ptr {operands[0]})");
                    Line($"{Result()} = load {Type(type)}, ptr {destination}{StorageAlign(type)}");
                    return true;
                }

                // The value copied over what the storage holds, by the metadata's assignWithCopy.
                case "value_witness_assign": {
                    var type = builtin.Operands[2].Type;
                    var source = Slot(type, operands[2]);
                    var witness = Witness(operands[0], AssignWitness);
                    Line($"call void {witness}(ptr {operands[1]}, ptr {source}, ptr {operands[0]})");
                    return true;
                }

                // The value destroyed, by the metadata's destroy.
                case "value_witness_destroy": {
                    var source = Slot(builtin.Operands[1].Type, operands[1]);
                    var witness = Witness(operands[0], DestroyWitness);
                    Line($"call void {witness}(ptr {source}, ptr {operands[0]})");
                    return true;
                }

                // What an existential's box holds: its dynamic type's metadata.
                case "box_metadata":
                    Line($"{Result()} = call ptr @sable_box_value_metadata(ptr {operands[0]})");
                    return true;

                case "witness_table_exists":
                    Line($"{Result()} = icmp ne ptr {operands[0]}, null");
                    return true;
            }

            // The registry: the witness table of the type's conformance to the protocol, or null.
            if (builtin.Name.StartsWith("witness_table:", StringComparison.Ordinal)) {
                Line($"{Result()} = call ptr @sable_witness_table(ptr {operands[0]}, ptr {module.ProtocolDescriptor(builtin.Name["witness_table:".Length..])})");
                return true;
            }

            // A requirement's witness, from a witness table.
            if (builtin.Name.StartsWith("witness_entry:", StringComparison.Ordinal)) {
                var index = int.Parse(builtin.Name["witness_entry:".Length..], CultureInfo.InvariantCulture);
                var at = Fresh();
                Line($"{at} = getelementptr inbounds {WitnessTableRecord}, ptr {operands[0]}, i32 0, i32 2");
                var requirements = Fresh();
                Line($"{requirements} = load ptr, ptr {at}");
                var slot = Fresh();
                Line($"{slot} = getelementptr inbounds ptr, ptr {requirements}, i64 {index}");
                Line($"{Result()} = load ptr, ptr {slot}");
                return true;
            }

            return false;
        }
    }
}
